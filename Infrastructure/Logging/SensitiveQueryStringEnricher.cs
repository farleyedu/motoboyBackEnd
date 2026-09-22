using System;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace APIBack.Infrastructure.Logging
{
    /// <summary>
    /// O SignalR envia o JWT na query string do WebSocket (`?access_token=...`) e o log
    /// de requisicao do ASP.NET Core ("Request starting ... {QueryString}") gravava o
    /// token inteiro: qualquer um com acesso aos logs da Render tinha um token valido.
    /// Este enricher mascara o valor em qualquer propriedade textual do evento.
    /// </summary>
    public sealed class SensitiveQueryStringEnricher : ILogEventEnricher
    {
        private static readonly Regex SensitiveParameter = new(
            @"(?<name>(?:access_token|refresh_token|token|password|senha)=)[^&\s""']+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            foreach (var property in logEvent.Properties.ToList())
            {
                if (property.Value is ScalarValue { Value: string text } && ContainsSensitive(text))
                {
                    logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(Redact(text))));
                }
            }
        }

        public static string Redact(string text) =>
            SensitiveParameter.Replace(text, match => $"{match.Groups["name"].Value}[redacted]");

        private static bool ContainsSensitive(string text) =>
            text.IndexOf('=') >= 0 && SensitiveParameter.IsMatch(text);
    }
}
