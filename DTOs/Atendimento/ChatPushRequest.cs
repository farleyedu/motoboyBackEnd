namespace APIBack.DTOs.Atendimento;
public sealed class ChatPushRequest
{
    public string Token {get;set;}="";
    public bool Sound {get;set;}=true;
    public bool Vibration {get;set;}=true;
    public bool Muted {get;set;}
    public bool MentionAlerts {get;set;}=true;
}
