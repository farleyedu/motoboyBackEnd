namespace APIBack.Model
{
    public class Motoboy
    {
        public int? Id { get; set; }
        public string? Nome { get; set; }
        public string? Avatar { get; set; }
        public string? Cnh { get; set; }
        public string? Telefone { get; set; }
        public string? PlacaMoto { get; set; }
        public string? MarcaMoto { get; set; }
        public string? ModeloMoto { get; set; }
        public string? RenavamMoto { get; set; }
        public int Status { get; set; }
        public int QtdPedidosAtivos { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }

        // Cadastro estruturado (Fase A do fluxo de motoboy). Pertence a linha canonica.
        public string? Cpf { get; set; }
        public DateOnly? DataNascimento { get; set; }
        public string? Cep { get; set; }
        public string? Logradouro { get; set; }
        public string? Numero { get; set; }
        public string? Complemento { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Uf { get; set; }
        public string? TipoVeiculo { get; set; }
        public string? CnhNumero { get; set; }
        public string? CnhCategoria { get; set; }
        public DateOnly? CnhValidade { get; set; }
        public string? PixTipo { get; set; }
        public string? PixChave { get; set; }
        public string? BancoNome { get; set; }
        public string? BancoAgencia { get; set; }
        public string? BancoConta { get; set; }

        /// <summary>Status administrativo (ativo/inativo/bloqueado). Separado de Status (presenca online/offline/delivering).</summary>
        public string StatusCadastro { get; set; } = "ativo";
    }
}
