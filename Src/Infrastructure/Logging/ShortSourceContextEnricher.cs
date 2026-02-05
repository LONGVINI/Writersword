using Serilog.Core;
using Serilog.Events;

namespace Writersword.Infrastructure.Logging
{
    public class ShortSourceContextEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContext))
            {
                if (sourceContext is ScalarValue scalar && scalar.Value is string fullName)
                {
                    var shortName = fullName.Split('.')[^1];
                    var shortProperty = propertyFactory.CreateProperty("ShortSourceContext", shortName);
                    logEvent.AddPropertyIfAbsent(shortProperty);
                }
            }
        }
    }
}