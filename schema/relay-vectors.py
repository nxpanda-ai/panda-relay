"""Generates schema/relay-vectors.json — an independent (third) implementation of the relay tunnel (plan-relay seam S2)
and the dial tokens / relayId (S1, S7), in the Python standard library only (P-256, HKDF, AES-256-GCM written out
below and self-checked against FIPS-197 / NIST GCM / curve constants before anything is written).

Run: python schema/relay-vectors.py
The C# (bridge) and TypeScript (extension) tunnel primitives must both reproduce every vector.
"""
import base64
import hashlib
import hmac
import json
import pathlib
import struct

OUT = pathlib.Path(__file__).with_name("relay-vectors.json")

# ---------------------------------------------------------------- P-256 (SEC 2 / FIPS 186-4 D.1.2.3)
P = 2**256 - 2**224 + 2**192 + 2**96 - 1
A = P - 3
B = 0x5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B
N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
G = (0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296,
     0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5)


def on_curve(pt):
    x, y = pt
    return 0 <= x < P and 0 <= y < P and (y * y - (x * x * x + A * x + B)) % P == 0


def ec_add(p1, p2):
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    (x1, y1), (x2, y2) = p1, p2
    if x1 == x2 and (y1 + y2) % P == 0:
        return None
    if p1 == p2:
        lam = (3 * x1 * x1 + A) * pow(2 * y1, -1, P) % P
    else:
        lam = (y2 - y1) * pow(x2 - x1, -1, P) % P
    x3 = (lam * lam - x1 - x2) % P
    return x3, (lam * (x1 - x3) - y1) % P


def ec_mul(k, pt):
    acc = None
    while k:
        if k & 1:
            acc = ec_add(acc, pt)
        pt = ec_add(pt, pt)
        k >>= 1
    return acc


def point_bytes(pt):  # SEC 1 uncompressed
    return b"\x04" + pt[0].to_bytes(32, "big") + pt[1].to_bytes(32, "big")


def ecdh_x(d, peer):  # raw x-coordinate = WebCrypto deriveBits(ECDH, 256) = .NET DeriveRawSecretAgreement
    return ec_mul(d, peer)[0].to_bytes(32, "big")


# ---------------------------------------------------------------- AES-256 (FIPS-197) + GCM (SP 800-38D)
def _sbox():
    def mul(a, b):
        r = 0
        while b:
            if b & 1:
                r ^= a
            a = ((a << 1) ^ 0x11B) & 0xFF if a & 0x80 else a << 1
            b >>= 1
        return r

    box = []
    for x in range(256):
        inv = next((y for y in range(1, 256) if mul(x, y) == 1), 0)
        s = inv
        for i in range(1, 5):
            s ^= ((inv << i) | (inv >> (8 - i))) & 0xFF
        box.append(s ^ 0x63)
    return box, mul


SBOX, GMUL8 = _sbox()


def aes_expand(key):
    assert len(key) == 32
    w = [list(key[i:i + 4]) for i in range(0, 32, 4)]
    rcon = 1
    for i in range(8, 60):
        t = list(w[i - 1])
        if i % 8 == 0:
            t = [SBOX[b] for b in t[1:] + t[:1]]
            t[0] ^= rcon
            rcon = GMUL8(rcon, 2)
        elif i % 8 == 4:
            t = [SBOX[b] for b in t]
        w.append([a ^ b for a, b in zip(w[i - 8], t)])
    return [sum(w[r * 4:r * 4 + 4], []) for r in range(15)]


def aes_encrypt_block(rk, block):
    s = [a ^ b for a, b in zip(block, rk[0])]
    for rnd in range(1, 15):
        s = [SBOX[b] for b in s]
        s = [s[(c * 4 + r + r * 4) % 16] for c in range(4) for r in range(4)]  # ShiftRows (column-major)
        if rnd != 14:
            m = []
            for c in range(4):
                a = s[c * 4:c * 4 + 4]
                m += [GMUL8(a[0], 2) ^ GMUL8(a[1], 3) ^ a[2] ^ a[3], a[0] ^ GMUL8(a[1], 2) ^ GMUL8(a[2], 3) ^ a[3],
                      a[0] ^ a[1] ^ GMUL8(a[2], 2) ^ GMUL8(a[3], 3), GMUL8(a[0], 3) ^ a[1] ^ a[2] ^ GMUL8(a[3], 2)]
            s = m
        s = [a ^ b for a, b in zip(s, rk[rnd])]
    return bytes(s)


def _ghash_mul(x, y):
    z, v = 0, y
    for i in range(128):
        if (x >> (127 - i)) & 1:
            z ^= v
        v = (v >> 1) ^ (0xE1 << 120) if v & 1 else v >> 1
    return z


def gcm_encrypt(key, iv, pt, aad):
    assert len(iv) == 12
    rk = aes_expand(key)
    h = int.from_bytes(aes_encrypt_block(rk, b"\x00" * 16), "big")
    j0 = iv + b"\x00\x00\x00\x01"
    ct = bytearray()
    for i in range(0, len(pt), 16):
        ctr = iv + struct.pack(">I", 2 + i // 16)
        ks = aes_encrypt_block(rk, ctr)
        ct += bytes(a ^ b for a, b in zip(pt[i:i + 16], ks))

    def pad(b):
        return b + b"\x00" * (-len(b) % 16)

    data = pad(aad) + pad(bytes(ct)) + struct.pack(">QQ", len(aad) * 8, len(ct) * 8)
    g = 0
    for i in range(0, len(data), 16):
        g = _ghash_mul(g ^ int.from_bytes(data[i:i + 16], "big"), h)
    tag = bytes(a ^ b for a, b in zip(g.to_bytes(16, "big"), aes_encrypt_block(rk, j0)))
    return bytes(ct) + tag


def _self_check():
    assert on_curve(G) and ec_mul(N, G) is None, "P-256 constants"
    rk = aes_expand(bytes(range(32)))  # FIPS-197 C.3
    assert aes_encrypt_block(rk, bytes.fromhex("00112233445566778899aabbccddeeff")).hex() == \
        "8ea2b7ca516745bfeafc49904b496089", "AES-256"
    z = b"\x00" * 32  # NIST GCM test cases 13 and 14
    assert gcm_encrypt(z, b"\x00" * 12, b"", b"").hex() == "530f8afbc74536b9a963b4f1c4cb738b", "GCM 13"
    assert gcm_encrypt(z, b"\x00" * 12, b"\x00" * 16, b"").hex() == \
        "cea7403d4d606b6e074ec5d3baf39d18d0d1c8a799996bf0265b98b5d48ab919", "GCM 14"
    k16 = bytes.fromhex("feffe9928665731c6d6a8f9467308308" * 2)  # NIST GCM test case 16: AAD + a partial block
    p16 = bytes.fromhex("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a72"
                        "1c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39")
    assert gcm_encrypt(k16, bytes.fromhex("cafebabefacedbaddecaf888"), p16,
                       bytes.fromhex("feedfacedeadbeeffeedfacedeadbeefabaddad2")).hex() == \
        "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e63" \
        "93ba7a0abcc9f662" "76fc6ece0f4e1768cddf8853bb2d551b", "GCM 16"
    # RFC 5869 A.1 (HKDF-SHA256)
    prk = hkdf_extract(bytes.fromhex("000102030405060708090a0b0c"), b"\x0b" * 22)
    assert hkdf_expand(prk, bytes.fromhex("f0f1f2f3f4f5f6f7f8f9"), 42).hex() == \
        "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF"


# ---------------------------------------------------------------- HKDF (RFC 5869)
def hkdf_extract(salt, ikm):
    return hmac.new(salt, ikm, hashlib.sha256).digest()


def hkdf_expand(prk, info, length):
    out, t, i = b"", b"", 1
    while len(out) < length:
        t = hmac.new(prk, t + info + bytes([i]), hashlib.sha256).digest()
        out += t
        i += 1
    return out[:length]


_self_check()


# ---------------------------------------------------------------- seam S2: handshake + records
def b64u(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).decode().rstrip("=")


LABEL = b"panda-relay-v1"
DIR_B2E, DIR_E2B = 1, 2


def keys_for(kind, psk, ikm, th):
    salt = hashlib.sha256(b"panda-relay-psk-v1" + bytes([kind]) + psk).digest()
    okm = hkdf_expand(hkdf_extract(salt, ikm), LABEL + bytes([kind]) + th, 96)
    return okm[:32], okm[32:64], okm[64:]


def handshake(kind, psk_b, psk_e, d_b, d_e, n_b, n_e):
    """Both sides' views; psk_b / psk_e may differ (negative vectors)."""
    e_b, e_e = point_bytes(ec_mul(d_b, G)), point_bytes(ec_mul(d_e, G))
    m1 = b"\x01" + bytes([kind]) + e_b + n_b
    m2_head = b"\x02" + e_e + n_e
    th = hashlib.sha256(m1 + m2_head).digest()
    ikm = ecdh_x(d_e, ec_mul(d_b, G))
    assert ikm == ecdh_x(d_b, ec_mul(d_e, G))
    b2e, e2b, conf_e = keys_for(kind, psk_e, ikm, th)
    mac_e = hmac.new(conf_e, b"ext" + th, hashlib.sha256).digest()
    m2 = m2_head + mac_e
    _, _, conf_b = keys_for(kind, psk_b, ikm, th)
    mac_b = hmac.new(conf_b, b"bridge" + th + mac_e, hashlib.sha256).digest()
    return {"eB": e_b, "eE": e_e, "m1": m1, "m2": m2, "m3": b"\x03" + mac_b, "th": th, "ikm": ikm,
            "kB2E": b2e, "kE2B": e2b, "kConf": conf_e}


def seal(key, direction, counter, rtype, body):
    c = struct.pack(">Q", counter)
    aad = LABEL + bytes([direction]) + c
    return b"\x10" + c + gcm_encrypt(key, b"\x00" * 4 + c, bytes([rtype]) + body, aad)


def seal_untyped(key, direction, counter):  # a record whose plaintext lacks even the type byte
    c = struct.pack(">Q", counter)
    return b"\x10" + c + gcm_encrypt(key, b"\x00" * 4 + c, b"", LABEL + bytes([direction]) + c)


def close_body(code, reason):
    return struct.pack(">H", code) + reason.encode("utf-8")


def normalize(code: str):  # same rule as schema/auth-vectors.py (spec §71.3)
    n = "".join(chr(ord(c) - 32) if "a" <= c <= "z" else c for c in code).replace(" ", "").replace("-", "")
    ok = len(n) == 26 and all(c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567" for c in n)
    return n if ok else None


SPKI_P256_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d030107034200")

code = base64.b32encode(bytes(range(100, 116))).decode().rstrip("=")
code_display = "-".join(code[i:i + 4] for i in range(0, 26, 4))
secret = bytes(range(32))
d_b = int.from_bytes(hashlib.sha256(b"vector bridge ephemeral").digest(), "big") % N
d_e = int.from_bytes(hashlib.sha256(b"vector ext ephemeral").digest(), "big") % N
d_x = int.from_bytes(hashlib.sha256(b"vector other ephemeral").digest(), "big") % N
n_b, n_e = bytes(range(16)), bytes(range(16, 32))


def scalar(d):
    return d.to_bytes(32, "big")


def hs_vector(kind, psk, why=None):
    h = handshake(kind, psk, psk, d_b, d_e, n_b, n_e)
    return {"kind": kind, "pskHex": psk.hex(),
            "dB": scalar(d_b).hex(), "dE": scalar(d_e).hex(), "nB": n_b.hex(), "nE": n_e.hex(),
            **{k: v.hex() for k, v in h.items()}}


pair_psk = normalize(code_display).encode("ascii")
handshakes = [hs_vector(1, pair_psk), hs_vector(2, secret)]

records = []
for hs in handshakes:
    kb, ke = bytes.fromhex(hs["kB2E"]), bytes.fromhex(hs["kE2B"])
    # stateless record layer (sealRecord / openRecord); a session refuses a type-3 record that is not exactly 256 KiB
    rows = [(DIR_B2E, 0, 1, b'{"type":"HELLO"}'), (DIR_B2E, 1, 3, b"x" * 40), (DIR_B2E, 2, 1, b""),
            (DIR_E2B, 0, 1, "trang chủ — ok".encode("utf-8")), (DIR_E2B, 1, 2, close_body(4007, "replaced")),
            (DIR_E2B, 2**32 + 5, 1, b"counter above 2^32")]
    for direction, counter, rtype, body in rows:
        key = kb if direction == DIR_B2E else ke
        records.append({"kind": hs["kind"], "dir": direction, "counter": counter, "type": rtype,
                        "bodyHex": body.hex(), "recordHex": seal(key, direction, counter, rtype, body).hex()})

# Each refused record goes to a fresh extension-side session (expects counter 0 from the bridge). `reason` is the
# failure both implementations must report; `afterCounter` is the counter the receiver would expect next if it were
# still alive (1 when the record decrypted before being refused) — the test seals its follow-up record there, so only
# the "dead" flag can refuse it.
link = handshakes[1]
kb, ke = bytes.fromhex(link["kB2E"]), bytes.fromhex(link["kE2B"])
good = seal(kb, DIR_B2E, 0, 1, b"hi")
flip = bytearray(good)
flip[-1] ^= 1
flip_ct = bytearray(good)
flip_ct[9] ^= 1


def neg(why, record, reason, after=0):
    return {"why": why, "dir": DIR_B2E, "recordHex": record.hex(), "reason": reason, "afterCounter": after}


record_negative = [
    neg("tag bit flipped", bytes(flip), "bad record tag"),
    neg("ciphertext bit flipped", bytes(flip_ct), "bad record tag"),
    neg("counter 1 where 0 is expected (gap)", seal(kb, DIR_B2E, 1, 1, b"hi"), "bad counter"),
    neg("sealed with the other direction's key (reflection)", seal(ke, DIR_E2B, 0, 1, b"hi"), "bad record tag"),
    neg("sealed with the right key but the other direction byte", seal(kb, DIR_E2B, 0, 1, b"hi"), "bad record tag"),
    neg("unknown record type 4", seal(kb, DIR_B2E, 0, 4, b"hi"), "unknown record type"),
    neg("25-byte frame: no room for the type byte", seal_untyped(kb, DIR_B2E, 0), "bad record frame"),
    neg("truncated (shorter than header + tag)", good[:24], "bad record frame"),
    neg("wrong frame tag 0x11", b"\x11" + good[1:], "bad record frame"),
    neg("handshake frame M3 after the handshake", b"\x03" + b"\x00" * 32, "bad record frame"),
    neg("close record shorter than its code", seal(kb, DIR_B2E, 0, 2, b"\x0f"), "bad close record", 1),
    neg("close reason over 123 bytes", seal(kb, DIR_B2E, 0, 2, close_body(1000, "r" * 124)), "bad close record", 1),
    neg("close reason not UTF-8", seal(kb, DIR_B2E, 0, 2, b"\x03\xe8\xff"), "bad close record", 1),
    neg("close code 999", seal(kb, DIR_B2E, 0, 2, close_body(999, "")), "bad close record", 1),
    neg("close code 5000", seal(kb, DIR_B2E, 0, 2, close_body(5000, "")), "bad close record", 1),
    neg("text body not UTF-8", seal(kb, DIR_B2E, 0, 1, b"\xc3"), "text not UTF-8", 1),
    neg("non-final fragment shorter than 256 KiB", seal(kb, DIR_B2E, 0, 3, b"x" * 40), "bad fragment", 1),
    neg("empty non-final fragment", seal(kb, DIR_B2E, 0, 3, b""), "bad fragment", 1),
]

# Handshake refusals: `stage` is the step that must refuse (M1 = the responder refuses M1, M2 = the initiator refuses
# M2, M3 = the responder accepts M1 and refuses M3), with the link vector's fixed keys and nonces.
bad_m2 = handshake(2, secret, bytes(range(1, 33)), d_b, d_e, n_b, n_e)  # extension holds another secret
bad_m3 = handshake(2, bytes(range(1, 33)), secret, d_b, d_e, n_b, n_e)  # bridge holds another secret
other_kind = handshake(1, secret, secret, d_b, d_e, n_b, n_e)  # same bytes, kind 1
m1 = bytes.fromhex(link["m1"])
m2 = bytes.fromhex(link["m2"])
m3 = bytes.fromhex(link["m3"])
off_curve = bytearray(m1)
off_curve[-17] ^= 1  # last byte of eB.y
off_curve_m2 = bytearray(m2)
off_curve_m2[65] ^= 1  # last byte of eE.y
assert not on_curve((int.from_bytes(off_curve[3:35], "big"), int.from_bytes(off_curve[35:67], "big")))
assert not on_curve((int.from_bytes(off_curve_m2[2:34], "big"), int.from_bytes(off_curve_m2[34:66], "big")))


def hneg(why, stage, reason, **frames):
    return {"why": why, "stage": stage, "expectKind": 2, "pskHex": secret.hex(), "reason": reason,
            **{k: v.hex() for k, v in frames.items()}}


handshake_negative = [
    hneg("M1 kind 1 to a responder expecting kind 2", "M1", "kind mismatch", m1Hex=other_kind["m1"]),
    hneg("M1 kind 3", "M1", "kind mismatch", m1Hex=b"\x01\x03" + m1[2:]),
    hneg("M1 point not on the curve", "M1", "peer point invalid", m1Hex=bytes(off_curve)),
    hneg("M1 point in compressed form (0x02 prefix)", "M1", "peer point invalid",
         m1Hex=b"\x01\x02\x02" + m1[3:35] + b"\x00" * 32 + n_b),
    hneg("M1 truncated", "M1", "bad M1", m1Hex=m1[:-1]),
    hneg("M1 one byte too long", "M1", "bad M1", m1Hex=m1 + b"\x00"),
    hneg("M1 with frame tag 0x02", "M1", "bad M1", m1Hex=b"\x02" + m1[1:]),
    hneg("M2 MAC made with another PSK", "M2", "bad M2 MAC", m2Hex=bad_m2["m2"]),
    hneg("M2 truncated", "M2", "bad M2", m2Hex=m2[:-1]),
    hneg("M2 with frame tag 0x03", "M2", "bad M2", m2Hex=b"\x03" + m2[1:]),
    hneg("M2 point not on the curve", "M2", "peer point invalid", m2Hex=bytes(off_curve_m2)),
    hneg("M2 echoing the initiator's own point and nonce, zero MAC", "M2", "bad M2 MAC",
         m2Hex=b"\x02" + bytes.fromhex(link["eB"]) + n_b + b"\x00" * 32),
    hneg("M3 MAC made with another PSK", "M3", "bad M3 MAC", m1Hex=bad_m3["m1"], m3Hex=bad_m3["m3"]),
    hneg("M3 truncated", "M3", "bad M3", m1Hex=m1, m3Hex=m3[:-1]),
    hneg("M3 with frame tag 0x01", "M3", "bad M3", m1Hex=m1, m3Hex=b"\x01" + m3[1:]),
]

# ---------------------------------------------------------------- S7 dial tokens, S1 relayId
spki = SPKI_P256_PREFIX + point_bytes(ec_mul(d_x, G))
relay_id = b64u(hashlib.sha256(spki).digest())[:22]


def b64u_decode(t: str) -> bytes:
    return base64.urlsafe_b64decode(t + "=" * (-len(t) % 4))


def dial_token(key: bytes, label: str, rid: str) -> str:
    return b64u(hmac.new(key, label.encode("ascii") + rid.encode("ascii"), hashlib.sha256).digest())


link_token = dial_token(secret, "panda-relay-dial-link-v1", relay_id)
pair_token = dial_token(pair_psk, "panda-relay-dial-pair-v1", relay_id)
vectors = {
    "description": "Relay tunnel + dial-token vectors (plan-relay seams S1, S2, S7). Generated by "
                   "schema/relay-vectors.py — do not edit.",
    "handshakes": handshakes,
    "handshakeNegative": handshake_negative,
    "records": records,
    "recordNegative": record_negative,
    "relayId": [{"spkiHex": spki.hex(), "relayId": relay_id}],
    "dialTokens": [
        {"kind": "link", "secretHex": secret.hex(), "relayId": relay_id, "token": link_token,
         "tokenHash": b64u(hashlib.sha256(b64u_decode(link_token)).digest())},
        {"kind": "pair", "code": code_display, "relayId": relay_id, "token": pair_token,
         "tokenHash": b64u(hashlib.sha256(b64u_decode(pair_token)).digest())},
    ],
}
OUT.write_text(json.dumps(vectors, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
print(f"wrote {OUT} ({len(handshakes)} handshakes, {len(records)} records, "
      f"{len(record_negative) + len(handshake_negative)} negatives)")
