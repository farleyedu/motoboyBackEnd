using System.Linq;
using APIBack.DTOs.Cardapio;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class CardapioEnderecoConsultaTests
    {
        [Theory]
        [InlineData("Rua Alameda Dos Mandarins", "Alameda Dos Mandarins")]
        [InlineData("Rua Avenida Brasil", "Avenida Brasil")]
        [InlineData("Rua das Flores", "Rua das Flores")]
        [InlineData("Alameda Dos Mandarins", "Alameda Dos Mandarins")]
        [InlineData("Rua Alameda", "Rua Alameda")]
        public void LimparLogradouro_tira_o_tipo_de_via_repetido(string entrada, string esperado) =>
            Assert.Equal(esperado, CardapioPublicService.LimparLogradouro(entrada));

        [Fact]
        public void Consultas_vao_do_preciso_ao_aproximado_e_usam_o_cep()
        {
            var consultas = CardapioPublicService.MontarConsultasEndereco(new CardapioEnderecoArmazenado
            {
                Logradouro = "Rua Alameda Dos Mandarins", Numero = "500", Bairro = "Grand Ville",
                Cidade = "Uberlândia", Uf = "MG", Cep = "38407661"
            });

            Assert.Equal("Alameda Dos Mandarins, 500, Grand Ville, Uberlândia - MG, Brasil", consultas.First());
            Assert.Contains("Alameda Dos Mandarins, 500, Uberlândia - MG, Brasil", consultas);
            Assert.Equal("38407-661, Uberlândia - MG, Brasil", consultas.Last());
        }

        [Fact]
        public void Sem_numero_nem_cep_ainda_tenta_pela_rua()
        {
            var consultas = CardapioPublicService.MontarConsultasEndereco(new CardapioEnderecoArmazenado
            {
                Logradouro = "Rua A", Bairro = "Centro", Cidade = "Uberlândia", Uf = "MG"
            });

            Assert.Single(consultas);
        }
    }
}
