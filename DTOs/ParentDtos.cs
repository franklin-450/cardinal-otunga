using System;

namespace EduTrackTrial.DTOs
{
    public class StudentLoginRequest
    {
        public string? AccountNo { get; set; }
        public string? AccountNumber { get; set; }
        public string? StudentName { get; set; }
    }

    public class PaymentInitiateRequest
    {
        public int StudentId { get; set; }
        public string? AccountNo { get; set; }
        public string? AccountNumber { get; set; }
        public int Amount { get; set; }
        public string? Phone { get; set; }
        public string? PhoneNumber { get; set; }
    }

    public class MarkNotificationsRequest
    {
        public string? AccountNumber { get; set; }
    }

    public class FeeInfoDto
    {
        public int Term1 { get; set; }
        public int Term2 { get; set; }
        public int Term3 { get; set; }
        public int TotalDue { get; set; }
        public int AmountPaid { get; set; }
        public int Balance { get; set; }
    }

    public class StudentDto
    {
        public int Id { get; set; }
        public string? AccountNo { get; set; }
        public string? FullName { get; set; }
        public DateTime DateOfBirth { get; set; }
        public string? Gender { get; set; }
        public string? Grade { get; set; }
        public string? Stream { get; set; }
        public DateTime AdmissionDate { get; set; }
        public string? PreviousSchool { get; set; }
        public string? PhotoPath { get; set; }
        public string? MedicalInfo { get; set; }
        public string? Status { get; set; }
        public FeeInfoDto? Fees { get; set; }
        public FeeInfoDto? FeeData { get; set; }
    }

    public class MpesaResultDto
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
    }
}