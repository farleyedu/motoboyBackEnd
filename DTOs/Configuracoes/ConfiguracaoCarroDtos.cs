namespace APIBack.DTOs.Configuracoes
{
    public class CarroEstabelecimentoDto
    {
        public string Id { get; set; } = string.Empty;
        public string Marca { get; set; } = string.Empty;
        public string Modelo { get; set; } = string.Empty;
        public bool Ativo { get; set; }
    }
}
