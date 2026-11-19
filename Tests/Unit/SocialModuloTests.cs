using APIBack.Model.Gestao;
using Xunit;

namespace APIBack.Tests.Unit;

public class SocialModuloTests
{
    [Fact]
    public void Modulo_Social_vai_e_volta_do_banco()
    {
        Assert.Contains("SOCIAL", EstabelecimentoModuleMapper.ToDatabaseModules(new[] { "Social" }, "restaurante"));
        Assert.Contains("Social", EstabelecimentoModuleMapper.ToUiModules("Loja", new[] { "SOCIAL" }));
    }
}
