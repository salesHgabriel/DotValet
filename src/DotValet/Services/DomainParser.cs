using System.Text;
using System.Text.RegularExpressions;

namespace DotValet.Services;

public readonly record struct ParsedDomain(string Host, int? Port);

public static partial class DomainParser
{
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    /// <summary>
    /// Aceita "app", "app.test", "app2.test:5172" ou "http://app.test/".
    /// Um nome sem ponto recebe o TLD configurado.
    /// </summary>
    public static bool TryParse(string? input, string tld, out ParsedDomain result, out string? error)
    {
        result = default;
        error = null;

        var value = (input ?? "").Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "http://", "https://" })
        {
            if (value.StartsWith(scheme, StringComparison.Ordinal))
                value = value[scheme.Length..];
        }
        var slash = value.IndexOf('/');
        if (slash >= 0)
            value = value[..slash];

        if (value.Length == 0)
        {
            error = "Informe um domínio, ex.: app ou app2.test:5172";
            return false;
        }

        int? port = null;
        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            var portText = value[(colon + 1)..];
            if (!int.TryParse(portText, out var p) || p is < 1 or > 65535)
            {
                error = $"Porta inválida: \"{portText}\"";
                return false;
            }
            port = p;
            value = value[..colon];
        }

        if (value is "localhost" || value.EndsWith(".localhost", StringComparison.Ordinal))
        {
            error = "Use um domínio próprio (ex.: app.test), não localhost.";
            return false;
        }

        if (!value.Contains('.'))
            value = $"{value}.{tld.Trim('.')}";

        foreach (var label in value.Split('.'))
        {
            if (!LabelRegex().IsMatch(label))
            {
                error = $"Domínio inválido: \"{value}\"";
                return false;
            }
        }

        result = new ParsedDomain(value, port);
        return true;
    }

    /// <summary>"MyCompany.Api_v2" → "mycompany-api-v2".</summary>
    public static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 63)
            slug = slug[..63].TrimEnd('-');
        return slug.Length == 0 ? "app" : slug;
    }
}
