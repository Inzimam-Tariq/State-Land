using System.ComponentModel.DataAnnotations;

namespace StateLand.Models.Auth
{
    public class ApplicantDetail
    {
        [Key]
        public long ApplicantId { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }       // plain text password
        public string CNIC { get; set; }
        public string MobileNo { get; set; }
        public string FullName { get; set; }
        public string? FatherName { get; set; }
        public string? Gender { get; set; }
        public DateTime? BirthDate { get; set; }
        public string? ResidentialAddress { get; set; }
        public DateTime? CNICIssuanceDate { get; set; }
        public DateTime? CNICExpiryDate { get; set; }
        public string? OtherMobileNo { get; set; }
        public int DivisionId { get; set; }
        public int DistrictId { get; set; }
        public int TehsilId { get; set; }
        public int MauzaId { get; set; }
        public string? CurrentOccupation { get; set; }
        public string? MartialStatus { get; set; }
        public string? EmailId { get; set; }
        public string? CNICFront { get; set; }
        public string? CNICBack { get; set; }
        public string? DomicileCertificate { get; set; }
        public string? RecentPhotograph { get; set; }
        public bool IsActive { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}

