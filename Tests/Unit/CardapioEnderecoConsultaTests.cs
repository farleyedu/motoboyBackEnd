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
        [InlineData("Alameda Dos Mandarins Número", "Alameda Dos Mandarins")]
        [InlineData("Rua Alameda Dos Mandarins numero", "Alameda Dos Mandarins")]
        [InlineData("Rua das Flores nº", "Rua das Flores")]
        public void LimparLogradouro_deixa_so_o_nome_da_via(string entrada, string esperado) =>
            Assert.Equal(esperado, CardapioPublicService.LimparLogradouro(entrada));

        [Fact]
        public void Consultas_vao_da_mais_completa_para_a_mais_enxuta_e_levam_o_cep()
        {
            var consultas = CardapioPublicService.MontarConsultasEndereco(new CardapioEnderecoArmazenado
            {
                Logradouro = "Rua Alameda Dos Mandarins", Numero = "500", Bairro = "Grand Ville",
                Cidade = "Uberlândia", Uf = "MG", Cep = "38407661"
            });

            Assert.Equal(2, consultas.Count);
            Assert.Equal("Alameda Dos Mandarins, 500, Grand Ville, Uberlândia - MG, Brasil", consultas[0].Endereco);
            Assert.Equal("Alameda Dos Mandarins, 500, Uberlândia - MG, Brasil", consultas[1].Endereco);
            Assert.All(consultas, c => Assert.Equal("38407661", c.Cep));
        }

        [Fact]
        public void Sem_bairro_e_sem_cep_so_ha_uma_consulta_e_ela_nao_leva_cep()
        {
            var consultas = CardapioPublicService.MontarConsultasEndereco(new CardapioEnderecoArmazenado
            {
                Logradouro = "Rua A", Numero = "10", Cidade = "Uberlândia", Uf = "MG"
            });

            Assert.Single(consultas);
            Assert.Null(consultas.Single().Cep);
        }
    }
}
