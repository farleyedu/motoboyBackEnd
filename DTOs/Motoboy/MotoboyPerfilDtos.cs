using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Motoboy
{
    /// <summary>Cadastro estruturado do motoboy (Fase A do fluxo de motoboy). Sempre a linha canonica.</summary>
    public class MotoboyPerfilDto
    {
        public int MotoboyId { get; set; }
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
        public bool CnhVencida { get; set; }
        public string? PixTipo { get; set; }
        public string? PixChave { get; set; }
        public string? BancoNome { get; set; }
        public string? BancoAgencia { get; set; }
        public string? BancoConta { get; set; }
        public string StatusCadastro { get; set; } = "ativo";
        public IReadOnlyCollection<MotoboyPerfilVinculoDto> Vinculos { get; set; } = Array.Empty<MotoboyPerfilVinculoDto>();
    }

    public class MotoboyPerfilVinculoDto
    {
        public Guid EstabelecimentoId { get; set; }
        public string? EstabelecimentoNome { get; set; }
        public bool Ativo { get; set; }
    }

    public class AtualizarMotoboyPerfilRequest
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
        public string? StatusCadastro { get; set; }
    }
}
