using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace EduTrackTrial.Models
{
    public class AdmissionApplicationViewModel
    {
        [Required]
        public string FirstName { get; set; }

        [Required]
        public string LastName { get; set; }

        [Required]
        public string Gender { get; set; }

        [Required]
        public DateTime DateOfBirth { get; set; }

        [Required]
        public string GradeApplied { get; set; }

        [Required]
        public string ParentPhone { get; set; }

        [Required]
        public string ParentEmail { get; set; }

        public string PreviousSchool { get; set; }

        public List<string> Genders { get; set; } = new();
        public List<string> Grades { get; set; } = new();
    }
}
