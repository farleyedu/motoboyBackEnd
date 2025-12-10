namespace APIBack.Automation.Dtos.Estabelecimentos
{
    public class DefinirEstabelecimentoAtivoResponse
    {
        public string Token { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public EstabelecimentoSelecionadoDto EstabelecimentoSelecionado { get; set; } = new();
    }
}
