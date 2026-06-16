namespace StateLand.Models.Auth
{
    public class CreateUserRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }       // plain text password
        public string FullName { get; set; }
        public int RoleId { get; set; }
    }
}
