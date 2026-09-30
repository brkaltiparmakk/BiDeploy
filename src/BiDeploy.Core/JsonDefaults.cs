using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiDeploy.Core
{
    public static class JsonDefaults
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
