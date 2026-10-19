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
