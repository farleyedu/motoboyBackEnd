using APIBack.DTOs.Configuracoes;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using System.Globalization;
using System.Text.Json;

namespace APIBack.Printing;

public static class PrintDocumentBuilder
{
 private static string Money(decimal? value) => value?.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) ?? "";
 public static string Origin(string origin) => origin switch { "ifood" => "IFOOD", "cardapio_web" => "CARDÁPIO WEB", "ia_whatsapp" => "WHATSAPP", "atendente" => "BALCÃO", "simulador" => "SIMULADOR", _ => "PRÓPRIO" };
 public static string Modality(PedidoDetalheDto order) => order.IdentidadeExterna?.OrderType switch
 { "TAKEOUT" => "retirada", "DINE_IN" => "local", _ => "entrega" };
 public static PrintDocument Build(PedidoDetalheDto order, BusinessSettingsDto shop, PrintTemplate model, PrintPrinter printer, bool reprint = false)
 {
  var official = order.PedidoIfood;
  if (order.Origem == "ifood" && (official == null || order.IdentidadeExterna?.DataIsCurrent != true))
   throw new DeliveryDomainException(409,"PRINT_IFOOD_STALE","Atualize os dados oficiais antes de imprimir.");
  var blocks = new List<PrintBlock>();
  void Add(string kind, string? value) { if (model.Blocos.Contains(kind) && !string.IsNullOrWhiteSpace(value)) blocks.Add(new(kind,value)); }
  Add("loja",string.Join("\n",new[] { shop.NomeFantasia, shop.CnpjLoja,
    string.Join(", ",new[]{shop.Logradouro,shop.Numero,shop.Bairro,shop.Cidade,shop.Uf}.Where(s=>!string.IsNullOrWhiteSpace(s))),shop.Telefone,shop.Email,shop.SiteUrl,shop.InstagramUrl,shop.FacebookUrl }.Where(s=>!string.IsNullOrWhiteSpace(s))));
  if (model.MostrarLogo && Uri.TryCreate(shop.UrlLogo,UriKind.Absolute,out var logo) && logo.Scheme=="https") blocks.Add(new("logo",logo.ToString()));
  Add("cliente",string.Join(" · ",new[] { official?.CustomerName ?? order.NomeCliente,official?.ContactNumber ?? order.TelefoneCliente }.Where(s=>!string.IsNullOrWhiteSpace(s))));
  if (Modality(order)=="entrega") Add("endereco",order.EnderecoEntrega);
  var lines = official != null ? official.Items.Select(i=>new PrintLine(i.Name,i.Quantity,i.TotalPrice,
    Options(i.Options).Concat(string.IsNullOrWhiteSpace(i.Observations)?[]:new[]{"Obs.: "+i.Observations}).ToArray())).ToArray()
    : order.Itens.Select(i=>new PrintLine(i.Nome,i.Quantidade,i.Total,
      i.Adicionais.Select(a=>$"+ {a.Quantidade} × {a.Nome}").Concat(string.IsNullOrWhiteSpace(i.Observacao)?[]:new[]{"Obs.: "+i.Observacao}).ToArray())).ToArray();
  if(model.Blocos.Contains("itens")) blocks.Add(new("itens",Itens:lines));
  Add("observacoes",official?.Observations ?? order.Observacoes);
  Add("totais",string.Join("\n",new[]{
    (official?.Subtotal ?? order.Subtotal) is decimal sub ? "Subtotal: "+Money(sub) : null,
    (official?.DeliveryFee ?? order.TaxaEntrega) is decimal fee ? "Entrega: "+Money(fee) : null,
    official?.AdditionalFees is decimal additional ? "Taxas adicionais: "+Money(additional):null,
    official?.BenefitsAmount is decimal benefits ? "Benefícios: "+Money(benefits):null,
    "TOTAL: "+Money(official?.OrderAmount ?? order.Total)}.Where(s=>s!=null)));
  var amount = official == null ? (order.StatusPagamento?.ToLowerInvariant() is "pago" or "paid" ? 0 : order.Total) : official.AmountToCollect;
  var payment = amount switch { 0 => official!=null?"JÁ PAGO ONLINE":"JÁ PAGO", null => "VALOR A COBRAR NÃO CONFIRMADO · confira o pedido", _ => "COBRAR "+Money(amount) };
  payment += official == null ? " · "+order.FormaPagamento : "\n"+string.Join("\n", official.Payments.Select(p=>string.Join(" · ",new[]{p.Type,p.Method,p.CardBrand,Money(p.Value),p.ChangeFor.HasValue?"Troco para "+Money(p.ChangeFor):null}.Where(s=>!string.IsNullOrWhiteSpace(s)))));
  if(official==null && order.Troco>0) payment+=" · Troco para "+Money(order.Troco);
  Add("pagamento",payment);
  if(!string.IsNullOrWhiteSpace(model.Rodape)) blocks.Add(new("rodape",model.Rodape));
  if(model.Tipo=="via_cliente") blocks.Add(new("fiscal","Não é documento fiscal"));
  return new(1,order.Id,order.Origem,official?.DisplayNumber is string number?$"{order.Id} · iFood #{number}":order.Id.ToString(),Modality(order),
    official?.CreatedAtUtc ?? CreatedAt(order.CriadoEm),reprint,model,printer,blocks.ToArray());
 }
 private static DateTimeOffset CreatedAt(DateTime? value) => value==null?DateTimeOffset.UtcNow:value.Value.Kind==DateTimeKind.Utc?new DateTimeOffset(value.Value):new DateTimeOffset(DateTime.SpecifyKind(value.Value,DateTimeKind.Unspecified),TimeSpan.FromHours(-3));
 private static IEnumerable<string> Options(JsonElement options, int depth=0)
 {
  if(options.ValueKind!=JsonValueKind.Array || depth>10) yield break;
  foreach(var option in options.EnumerateArray())
  {
   if(option.TryGetProperty("name",out var name) && name.ValueKind==JsonValueKind.String)
   {
    var qty=option.TryGetProperty("quantity",out var q)&&q.TryGetDecimal(out var n)?n:1;
    yield return $"{new string(' ',depth*2)}+ {qty} × {name.GetString()}";
   }
   if(option.TryGetProperty("options",out var child)) foreach(var line in Options(child,depth+1)) yield return line;
  }
 }
}
