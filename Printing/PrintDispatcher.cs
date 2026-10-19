using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Printing;

/// <summary>Consumes committed acceptances. Printer/network errors cannot roll back an order.</summary>
public sealed class PrintDispatcher(NpgsqlDataSource source, PrintStore store, IPedidoConsultaRepository orders)
{
 public async Task<bool> DispatchNext(CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  var acceptance=await db.QuerySingleOrDefaultAsync<Acceptance>("""
SELECT estabelecimento_id AS EstabelecimentoId,pedido_id AS PedidoId,criado_em AS CriadoEm FROM impressao_aceite
WHERE processado_em IS NULL AND proxima_em<=now() ORDER BY criado_em FOR UPDATE SKIP LOCKED LIMIT 1
""",transaction:tx);
  if(acceptance==null)return false;
  var config=await store.Config(acceptance.EstabelecimentoId);
  var order=await orders.GetAsync(acceptance.EstabelecimentoId,acceptance.PedidoId);
  var reason=!config.Automatica?"Impressão automática desligada.":order==null?"Pedido não encontrado.":order.Origem=="simulador"?"O simulador não imprime em impressoras reais.":null;
  var matches=order==null?[]:config.Regras.Where(r=>r.Ativa&&r.Gatilho=="aceitar"&&r.Origens.Contains(order.Origem)&&r.Modalidades.Contains(PrintDocumentBuilder.Modality(order))).OrderBy(r=>r.Ordem).ToArray();
  if(reason==null&&matches.Length==0)reason="Nenhuma regra imprime esta origem e modalidade.";
  if(reason!=null)await PrintStore.InsertJob(db,tx,acceptance.EstabelecimentoId,null,null,$"ignored:{acceptance.EstabelecimentoId}:{acceptance.PedidoId}","ignorado",null,reason,order?.Origem,acceptance.PedidoId);
  else
  {
   var shop=await store.Shop(acceptance.EstabelecimentoId);
   var stations=await store.Stations(acceptance.EstabelecimentoId);
   foreach(var rule in matches)
   {
    var printer=config.Impressoras.SingleOrDefault(p=>p.Id==rule.ImpressoraId&&p.Ativa&&stations.Any(s=>s.Id==p.EstacaoId&&s.Ativa));
    var template=config.Modelos.SingleOrDefault(m=>m.Id==rule.ModeloId&&!m.Arquivado);
    if(printer==null||template==null)
    {
     await PrintStore.InsertJob(db,tx,acceptance.EstabelecimentoId,null,rule,$"{acceptance.PedidoId}:{rule.Id}:missing","ignorado",null,"Regra sem impressora ou modelo ativo.",order!.Origem,order.Id);continue;
    }
    PrintDocument document;
    try { document=PrintDocumentBuilder.Build(order!,shop,template,printer); }
    catch(APIBack.Service.DeliveryDomainException error)
    {
     var key=$"{acceptance.EstabelecimentoId}:{acceptance.PedidoId}:{rule.Id}:document";
     await PrintStore.InsertJob(db,tx,acceptance.EstabelecimentoId,null,rule,key,"automatico",null,error.Message,order!.Origem,order.Id);
     await db.ExecuteAsync("UPDATE impressao_job SET estado='falhou',impressora_id=@printerId,estacao_id=@station WHERE chave_unica=@key",new{printerId=printer.Id,station=printer.EstacaoId,key},tx);
     continue;
    }
    for(var copy=1;copy<=rule.Vias;copy++)
     await PrintStore.InsertJob(db,tx,acceptance.EstabelecimentoId,document,rule,$"{acceptance.EstabelecimentoId}:{acceptance.PedidoId}:{rule.Id}:{copy}:aceitar","automatico",null,copy:copy,acceptedAt:new DateTimeOffset(DateTime.SpecifyKind(acceptance.CriadoEm,DateTimeKind.Utc)));
   }
  }
  await db.ExecuteAsync("UPDATE impressao_aceite SET processado_em=now() WHERE estabelecimento_id=@EstabelecimentoId AND pedido_id=@PedidoId",acceptance,tx);
  await tx.CommitAsync(ct);return true;
 }
 public sealed record Acceptance(Guid EstabelecimentoId,int PedidoId,DateTime CriadoEm);
}

public sealed class PrintWorker(IServiceScopeFactory scopes, NpgsqlDataSource source, ILogger<PrintWorker> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  while(!ct.IsCancellationRequested)
  {
   try
   {
    await using var scope=scopes.CreateAsyncScope();var dispatcher=scope.ServiceProvider.GetRequiredService<PrintDispatcher>();
    for(var i=0;i<50&&await dispatcher.DispatchNext(ct);i++){}
    await using var db=await source.OpenConnectionAsync(ct);
    await db.ExecuteAsync("""
WITH changed AS (
 UPDATE impressao_job j SET estado='expirado',motivo=CASE WHEN estado='entregue' THEN 'A estação recebeu o trabalho, mas não confirmou o envio. Confira a impressora antes de tentar de novo.'
 ELSE COALESCE((SELECT 'O computador '||e.nome||' está desconectado do Zippy. Tempo de impressão esgotado.' FROM impressao_estacao e WHERE e.id=j.estacao_id AND (e.ultimo_sinal_em IS NULL OR e.ultimo_sinal_em<now()-interval '15 seconds')),'Tempo de impressão esgotado.') END
 WHERE estado IN('pendente','entregue') AND expira_em<=now() RETURNING id,estabelecimento_id
)
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,aggregate_version,payload,occurred_at_utc)
SELECT gen_random_uuid(),'print.job.updated','establishment:'||replace(estabelecimento_id::text,'-',''),estabelecimento_id,1,jsonb_build_object('id',id),now() FROM changed;
-- A successful replacement resolves previous warnings, preserving their original history.
UPDATE impressao_job j SET descartado=true WHERE j.estado IN('falhou','expirado') AND NOT j.descartado
AND EXISTS(SELECT 1 FROM impressao_job n WHERE n.estabelecimento_id=j.estabelecimento_id AND n.pedido_id=j.pedido_id
AND n.impressora_id=j.impressora_id AND n.criado_em>j.criado_em AND n.estado IN('enviado','impresso'));
""");
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch(Exception ex)
   {
    logger.LogError(ex,"Falha ao processar fila de impressão; pedidos continuam aceitos.");
    // Backoff the oldest poisoned acceptance, so it cannot starve other stores/orders.
    try
    {
     await using var db=await source.OpenConnectionAsync(ct);
     await db.ExecuteAsync("""
UPDATE impressao_aceite SET tentativas=tentativas+1,proxima_em=now()+interval '30 seconds'
WHERE (estabelecimento_id,pedido_id) IN(SELECT estabelecimento_id,pedido_id FROM impressao_aceite WHERE processado_em IS NULL AND proxima_em<=now() ORDER BY criado_em LIMIT 1)
""");
    }catch(Exception retryError){logger.LogWarning(retryError,"Fila de impressão indisponível.");}
   }
   try{await Task.Delay(2000,ct);}catch(OperationCanceledException){break;}
  }
 }
}
