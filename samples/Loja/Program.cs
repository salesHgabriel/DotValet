using System.Runtime.InteropServices;
using Microsoft.AspNetCore.HttpOverrides;

// App de exemplo do Dot Valet: mostra em que endereço foi aberto (domínio .test) e qual runtime usa.
var app = WebApplication.CreateBuilder(args).Build();

// Atrás do proxy do Dot Valet o esquema original (https) chega em X-Forwarded-Proto.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });

app.MapGet("/", (HttpContext ctx) =>
{
    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var html = $$"""
        <!doctype html><html lang="pt-br"><head><meta charset="utf-8"><title>Loja · {{url}}</title>
        <style>
          body{margin:0;font-family:-apple-system,system-ui,Segoe UI,sans-serif;background:#f6f6f8;color:#1d1d1f}
          header{background:#e8590c;color:#fff;padding:36px 48px}
          header h1{margin:0;font-size:34px} header p{margin:6px 0 0;opacity:.9}
          .url{display:inline-block;margin-top:18px;background:rgba(255,255,255,.18);padding:8px 14px;border-radius:8px;
                font:600 20px ui-monospace,SFMono-Regular,Menlo,monospace}
          main{padding:32px 48px} ul{list-style:none;padding:0;margin:0;max-width:640px}
          li{display:flex;justify-content:space-between;background:#fff;margin-bottom:10px;padding:16px 20px;border-radius:10px;
              box-shadow:0 1px 2px rgba(0,0,0,.06)}
          footer{padding:0 48px;color:#6e6e73;font-size:14px}
        </style></head><body>
        <header><h1>🛒 Loja</h1><p>Catálogo de produtos — modo proxy (sem porta)</p><div class="url">{{url}}</div></header>
        <main><ul><li><span>Teclado mecânico</span><b>R$ 459,90</b></li><li><span>Mouse sem fio</span><b>R$ 189,00</b></li><li><span>Monitor 27"</span><b>R$ 1.799,00</b></li></ul></main>
        <footer>ASP.NET Core {{Environment.Version}} · {{RuntimeInformation.RuntimeIdentifier}} · servido pelo Dot Valet</footer>
        </body></html>
        """;
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();
