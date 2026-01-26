using System.Collections.Generic;

namespace EduTrackTrial.Models
{
    public class SubscriptionRequest
    {
        // ===== School Info =====
        public string SchoolName { get; set; }
        public string RegNo { get; set; }
        public string SchoolEmail { get; set; }
        public string Paybill { get; set; }
        public string SchoolGender { get; set; } // Boys | Girls | Mixed

        // ===== Admin Info =====
        public string AdminName { get; set; }
        public string AdminEmail { get; set; }
        public string AdminPhone { get; set; }
        public string Password { get; set; }

        // ===== Plan =====
        public int PlanAmount { get; set; }

        // ===== Grades (Streams optional) =====
        public List<GradeRequest> Grades { get; set; } = new();
    }

    public class GradeRequest
    {
        public string GradeName { get; set; }
        public int Term1 { get; set; }
        public int Term2 { get; set; }
        public int Term3 { get; set; }
        public string? Streams { get; set; } // OPTIONAL
    }
}
