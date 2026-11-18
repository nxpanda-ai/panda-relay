using System.Text;
using Microsoft.AspNetCore.Http;

namespace Panda.Relay;

/// <summary>
/// plan-https-code T2 (AC4, seam S3): what a browser shows for the connection code's link form
/// <c>https://&lt;relay&gt;/c/&lt;relayId&gt;#&lt;code&gt;</c>. A constant page: no script can run (CSP), the request is
/// never echoed (no relayId, no query), nothing is logged. The #code never reaches here — a browser does not send the
/// fragment, and nothing this page holds reads it.
/// </summary>
internal static class HelpPage
{
    public const string Prefix = "/c/";

    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""
        <!doctype html>
        <html lang="vi">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>Panda — mã kết nối / connection code</title>
        <style>
        body { font: 16px/1.55 system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; color: #1d2433; background: #fff; }
        h1 { font-size: 1.3rem; } h2 { font-size: 1.1rem; margin-top: 2rem; }
        code { background: #f1f3f7; padding: .1rem .35rem; border-radius: 4px; }
        .warn { background: #fff4e5; border: 1px solid #f0b46a; padding: .75rem 1rem; border-radius: 8px; }
        @media (prefers-color-scheme: dark) { body { color: #e6e9ef; background: #14171d; } code { background: #262b35; } .warn { background: #3a2a14; border-color: #8a5a20; } }
        </style>
        </head>
        <body>
        <section lang="vi">
        <h1>Đây là mã kết nối Panda</h1>
        <p class="warn">Link này dùng để <b>dán vào panda-browser</b>, không phải để mở trong trình duyệt. Đừng gửi tiếp cho người khác: ai dùng nó trước trong 5 phút sẽ điều khiển được Chrome đã tạo ra nó.</p>
        <p>Copy toàn bộ địa chỉ trên thanh địa chỉ (gồm cả phần sau dấu <code>#</code>), rồi chạy trên máy có Panda:</p>
        <p><code>panda-browser connect "&lt;địa chỉ vừa copy&gt;"</code></p>
        <p>Cần panda-browser 0.1.15 trở lên. Bản cũ hơn không nhận dạng link này: hãy xin người gửi mã dạng <code>panda-relay://</code>, hoặc cập nhật Panda.</p>
        </section>
        <section lang="en">
        <h2>This is a Panda connection code</h2>
        <p class="warn">This link is meant to be <b>pasted into panda-browser</b>, not opened in a browser. Do not pass it on: whoever uses it first within 5 minutes controls the Chrome that made it.</p>
        <p>Copy the whole address from the address bar (including the part after <code>#</code>), then run on the computer with Panda:</p>
        <p><code>panda-browser connect "&lt;the copied address&gt;"</code></p>
        <p>Needs panda-browser 0.1.15 or newer. An older one does not take this link: ask the sender for the <code>panda-relay://</code> code, or update Panda.</p>
        </section>
        </body>
        </html>
        """);

    /// <summary>GET / HEAD of <c>/c/&lt;relayId&gt;</c>: the page for a well-formed id (exact, case-sensitive path), else 404.</summary>
    public static Task ServeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal) || !IsRelayId(path.AsSpan(Prefix.Length)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = Body.Length;
        response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Robots-Tag"] = "noindex";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers.CacheControl = "public, max-age=3600"; // the same bytes for every id (D4)
        return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : response.Body.WriteAsync(Body, context.RequestAborted).AsTask();
    }

    private static bool IsRelayId(ReadOnlySpan<char> value)
    {
        if (value.Length != 22)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
