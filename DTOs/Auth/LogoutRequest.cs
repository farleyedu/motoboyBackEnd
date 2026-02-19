namespace APIBack.DTOs.Auth
{
    public class LogoutRequest
    {
        public string? RefreshToken { get; set; }
        public bool LogoutFromAllDevices { get; set; }
    }
}
