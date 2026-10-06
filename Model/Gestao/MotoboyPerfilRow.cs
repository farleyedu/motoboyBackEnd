using System;

namespace APIBack.Model.Gestao
{
    /// <summary>Linha canonica do cadastro de motoboy (Fase A). Resultado de query, nao exposto direto na API.</summary>
    public class MotoboyPerfilRow
    {
        public int MotoboyId { get; set; }
        public int? UsuarioId { get; set; }
        public string? Nome { get; set; }
        public string? Avatar { get; set; }
        public string? Telefone { get; set; }
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
        public string? PlacaMoto { get; set; }
        public string? MarcaMoto { get; set; }
        public string? ModeloMoto { get; set; }
        public string? RenavamMoto { get; set; }
        public string? CnhNumero { get; set; }
        public string? CnhCategoria { get; set; }
        public DateOnly? CnhValidade { get; set; }
        public string? PixTipo { get; set; }
        public string? PixChave { get; set; }
        public string? BancoNome { get; set; }
        public string? BancoAgencia { get; set; }
        public string? BancoConta { get; set; }
        public string StatusCadastro { get; set; } = "ativo";
    }

    public class MotoboyPerfilVinculoRow
    {
        public Guid EstabelecimentoId { get; set; }
        public string? EstabelecimentoNome { get; set; }
        public bool Ativo { get; set; }
    }

    /// <summary>Comando de atualizacao do cadastro, ja validado pelo service.</summary>
    public class MotoboyPerfilUpdateCommand
    {
        public string? Nome { get; set; }
        public string? Telefone { get; set; }
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
        public string? PlacaMoto { get; set; }
        public string? MarcaMoto { get; set; }
        public string? ModeloMoto { get; set; }
        public string? RenavamMoto { get; set; }
        public string? CnhNumero { get; set; }
        public string? CnhCategoria { get; set; }
        public DateOnly? CnhValidade { get; set; }
        public string? PixTipo { get; set; }
        public string? PixChave { get; set; }
        public string? BancoNome { get; set; }
        public string? BancoAgencia { get; set; }
        public string? BancoConta { get; set; }
        public string StatusCadastro { get; set; } = "ativo";
    }
}
