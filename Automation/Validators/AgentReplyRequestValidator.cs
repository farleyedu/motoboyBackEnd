// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Validators
{
    public class AgentReplyRequestValidator
    {
        public (bool Valido, string? Erro) Validar(AgentReplyRequest requisicao)
        {
            if (requisicao.IdConversa == default)
            {
                return (false, "IdConversa obrigatorio");
            }
            if (string.IsNullOrWhiteSpace(requisicao.Mensagem))
            {
                return (false, "Mensagem nao pode ser vazia");
            }
            return (true, null);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
