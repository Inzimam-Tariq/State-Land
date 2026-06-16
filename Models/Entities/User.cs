namespace StateLand.Models.Entities
{
    public class User
    {
        public int UserId { get; set; }
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public string FullName { get; set; }
        public int RoleId { get; set; }
        public bool PassswordUpdated { get; set; }
        public bool IsActive { get; set; }
        public string? MobileNo { get; set; }
        public DateTime CreatedOn { get; set; } = DateTime.Now;
        public DateTime? UpdatedOn { get; set; }
    }

    public class Assignment
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; }
        public int RoleId { get; set; }
        public int? DistrictId { get; set; }
        public string? DistrictName { get; set; }
        public int? TehsilId { get; set; }
        public string? TehsilName { get; set; }
        public bool IsActive { get; set; }
        public DateTime? CreatedOn { get; set; } = DateTime.Now;
        public DateTime? UpdatedOn { get; set; }
    }
}
