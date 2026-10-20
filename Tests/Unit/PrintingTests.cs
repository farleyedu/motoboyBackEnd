using APIBack.DTOs.Configuracoes;
using APIBack.DTOs.Delivery;
using APIBack.Printing;
using APIBack.Service;
using APIBack.Security;
using Xunit;

namespace APIBack.Tests.Unit;
public sealed class PrintingTests
{
 [Fact] public void DocumentDoesNotSerializeSecretDeliveryCodeAndOmitsMissingFields()
 {
  var doc=PrintDocumentBuilder.Build(new(){Id=42,Origem="ia_whatsapp",CodigoEntrega="SECRET-8811",NomeCliente="João",Itens=[new(){Nome="Ação",Quantidade=2,Adicionais=[new(){Nome="Queijo",Quantidade=2}]}],Total=20,FormaPagamento="dinheiro",Troco=50},new(){NomeFantasia="Café"},new(),new());
  var json=PrintStore.Serialize(doc);Assert.DoesNotContain("SECRET-8811",json);Assert.DoesNotContain("codigoEntrega",json);Assert.Contains("COBRAR",json);Assert.DoesNotContain("undefined",json);
  Assert.Contains(doc.Blocos,b=>b.Tipo=="itens"&&b.Itens![0].Detalhes[0].Contains("2"));
 }
 [Fact] public void PublishedInstallerIsAnnouncedAndDownloadRedirectsToTheRelease()
 {
  var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Printing:AgentVersion"]="1.0.0",["Printing:InstallerUrl"]="https://github.com/farleyedu/ZippyImpressora/releases/download/v1.0.0/ZippyPrintSetup.exe",["Printing:InstallerSha256"]=new string('a',64),["Printing:InstallerSize"]="162355214"}).Build();
  var env=new Moq.Mock<Microsoft.Extensions.Hosting.IHostEnvironment>();env.Setup(e=>e.ContentRootPath).Returns(Path.GetTempPath());
  var controller=new APIBack.Controllers.PrintController(null!,config,env.Object);
  var manifest=System.Text.Json.JsonSerializer.SerializeToElement(((Microsoft.AspNetCore.Mvc.OkObjectResult)controller.Version()).Value).GetProperty("data");
  Assert.True(manifest.GetProperty("disponivel").GetBoolean());Assert.Equal("/api/v2/delivery/impressao/agente/instalador",manifest.GetProperty("download").GetString());Assert.Equal(new string('A',64),manifest.GetProperty("sha256").GetString());Assert.Equal(162355214,manifest.GetProperty("tamanho").GetInt64());
  Assert.Equal("https://github.com/farleyedu/ZippyImpressora/releases/download/v1.0.0/ZippyPrintSetup.exe",Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectResult>(controller.Installer()).Url);
  var http=new APIBack.Controllers.PrintController(null!,new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Printing:InstallerUrl"]="http://inseguro/x.exe",["Printing:InstallerSha256"]=new string('a',64),["Printing:InstallerSize"]="1"}).Build(),env.Object);
  Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(http.Installer());
 }
 [Fact] public void StaleIfoodCannotGenerateReceipt()
 {
  var error=Assert.Throws<DeliveryDomainException>(()=>PrintDocumentBuilder.Build(new(){Origem="ifood",IdentidadeExterna=new(){DataIsCurrent=false}},new(),new(),new()));
  Assert.Equal("PRINT_IFOOD_STALE",error.Code);
 }
 [Theory][InlineData("balcao")][InlineData("moderno")][InlineData("caseiro")]
 public void StylesDoNotChangeTheContent(string style)
 {
  var doc=PrintDocumentBuilder.Build(new(){Id=42,Origem="atendente",Total=12,StatusPagamento="pago"},new(){NomeFantasia="Loja"},new(){Estilo=style,Tipo="via_cliente"},new());
  Assert.Contains(doc.Blocos,b=>b.Tipo=="pagamento"&&b.Texto=="JÁ PAGO · ");Assert.Contains(doc.Blocos,b=>b.Tipo=="fiscal");Assert.Equal("atendente",doc.Origem);
 }
 [Fact] public void InvalidOwnershipAndAutoWithoutPrinterAreRejected()
 {
  var config=new PrintConfig{Automatica=true,Modelos=[new()]};
  Assert.Throws<DeliveryDomainException>(()=>PrintStore.Validate(config,[]));
  config.Automatica=false;config.Impressoras=[new(){EstacaoId=Guid.NewGuid()}];
  Assert.Throws<DeliveryDomainException>(()=>PrintStore.Validate(config,[]));
 }
 [Fact] public void SeparateCapabilityIsAvailableForTeamDelegation()
 {Assert.Contains("configurar_impressao",EstablishmentPermissions.Catalog()["Delivery"]);}
}
