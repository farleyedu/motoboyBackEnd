using System.Linq;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class AcessoServiceTests
    {
        [Fact]
        public void Token_e_longo_unico_e_seguro_para_url()
        {
            var tokens = Enumerable.Range(0, 50).Select(_ => AcessoService.NovoToken()).ToList();

            Assert.Equal(tokens.Count, tokens.Distinct().Count());
            Assert.All(tokens, token =>
            {
                Assert.True(token.Length >= 43);
                Assert.DoesNotContain('+', token);
                Assert.DoesNotContain('/', token);
                Assert.DoesNotContain('=', token);
            });
        }

        [Fact]
        public void Hash_e_estavel_ignora_espacos_e_nao_devolve_o_token()
        {
            var token = AcessoService.NovoToken();

            Assert.Equal(AcessoService.HashToken(token), AcessoService.HashToken("  " + token + "\n"));
            Assert.NotEqual(AcessoService.HashToken(token), AcessoService.HashToken(AcessoService.NovoToken()));
            Assert.DoesNotContain(token, AcessoService.HashToken(token));
            Assert.Equal(64, AcessoService.HashToken(token).Length);
        }

        [Fact]
        public void Convite_vale_mais_que_a_redefinicao()
        {
            Assert.True(AcessoService.ValidadeConvite > AcessoService.ValidadeRedefinicao);
        }
    }
}
