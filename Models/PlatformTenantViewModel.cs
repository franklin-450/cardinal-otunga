namespace EduTrackTrial.Models
{
    public class PlatformTenantViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Subdomain { get; set; }
        public bool Verified { get; set; }
        public DateTime TrialEnds { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Email { get; set; }   // optional – only if you have it
    }
}
