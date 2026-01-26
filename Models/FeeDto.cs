using System.Text.Json.Serialization;

namespace EduTrackTrial.Models
{
    public class FeeDto
    {
        [JsonPropertyName("term1")]
        public int Term1 { get; set; }

        [JsonPropertyName("term2")]
        public int Term2 { get; set; }

        [JsonPropertyName("term3")]
        public int Term3 { get; set; }
    }
}
