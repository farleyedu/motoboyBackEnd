using APIBack.DTOs.Configuracoes;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Hubs;
using Dapper;
using Npgsql;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace APIBack.Printing;

public sealed class PrintStore(NpgsqlDataSource source, IPedidoConsultaRepository orders)
{
 public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
 public static string Serialize<T>(T value) => JsonSerializer.Serialize(value,Json);
 public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
 internal static DeliveryDomainException Error(string text,int status=422) => new(status,"PRINT_ERROR",text);
 private static async Task Event(NpgsqlConnection db,NpgsqlTransaction tx,Guid store,string name,object value)
 {
  await db.ExecuteAsync("""
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,aggregate_version,payload,occurred_at_utc)
VALUES(@Id,@Name,@Target,@Store,1,CAST(@Payload AS jsonb),now())
""",new{Id=Guid.NewGuid(),Name=name,Target=DeliveryRealtimeEvents.EstablishmentGroup(store),Store=store,Payload=Serialize(value)},tx);
 }
 public async Task<PrintConfig> Config(Guid store,bool initialize=false)
 {
  await using var db=await source.OpenConnectionAsync();
  if(initialize)
  {
   await using var tx=await db.BeginTransactionAsync();
   var created=await db.ExecuteAsync("INSERT INTO impressao_config(estabelecimento_id) VALUES(@store) ON CONFLICT DO NOTHING",new{store},tx);
   if(created>0)
   {
    var models=new[]{new PrintTemplate(),new PrintTemplate{Nome="Cozinha",Tipo="cozinha",Blocos=["loja","cliente","endereco","itens","observacoes"],TamanhoTexto=15},new PrintTemplate{Nome="Via do cliente",Tipo="via_cliente"}};
    foreach(var model in models) await Write(db,tx,"modelo",store,model.Id,model);
    var rule=new PrintRule{ModeloId=models[0].Id}; await Write(db,tx,"regra",store,rule.Id,rule);
   }
   await tx.CommitAsync();
  }
  var row=await db.QuerySingleOrDefaultAsync<(bool Automatica,long Revisao)?> ("SELECT automatica,revisao FROM impressao_config WHERE estabelecimento_id=@store",new{store});
  if(row==null) return new PrintConfig();
  return new(){Automatica=row.Value.Automatica,Revisao=row.Value.Revisao,
   Modelos=await Read<PrintTemplate>(db,"modelo",store),Impressoras=await Read<PrintPrinter>(db,"impressora",store),Regras=await Read<PrintRule>(db,"regra",store)};
 }
 private static async Task<List<T>> Read<T>(NpgsqlConnection db,string kind,Guid store) =>
  (await db.QueryAsync<string>($"SELECT dados::text FROM impressao_{kind} WHERE estabelecimento_id=@store",new{store})).Select(s=>JsonSerializer.Deserialize<T>(s,Json)!).ToList();
 private static Task Write<T>(NpgsqlConnection db,NpgsqlTransaction tx,string kind,Guid store,Guid id,T data) => db.ExecuteAsync(
  $"INSERT INTO impressao_{kind}(id,estabelecimento_id,dados) VALUES(@id,@store,CAST(@data AS jsonb)) ON CONFLICT(id) DO UPDATE SET dados=EXCLUDED.dados WHERE impressao_{kind}.estabelecimento_id=@store",
  new{id,store,data=Serialize(data)},tx);
 public static void Validate(PrintConfig config, IReadOnlyCollection<PrintStation> stations)
 {
  if(config.Modelos.Count is <1 or >100 || config.Impressoras.Count>100 || config.Regras.Count>100) throw Error("Quantidade de modelos, impressoras ou regras inválida.");
  if(config.Modelos.Select(m=>m.Id).Distinct().Count()!=config.Modelos.Count || config.Impressoras.Select(m=>m.Id).Distinct().Count()!=config.Impressoras.Count || config.Regras.Select(m=>m.Id).Distinct().Count()!=config.Regras.Count) throw Error("Há identificadores repetidos.");
  foreach(var m in config.Modelos)
   if(m.Id==Guid.Empty || string.IsNullOrWhiteSpace(m.Nome) || m.Nome.Length>100 || m.Tipo is not("caixa" or "cozinha" or "via_cliente") || m.Estilo is not("balcao" or "moderno" or "caseiro") || m.TamanhoTexto is <11 or >24 || m.Rodape.Length>300 || m.Blocos.Except(new[]{"loja","cliente","endereco","itens","observacoes","totais","pagamento"}).Any() || m.Separador is not("linha" or "pontilhado" or "duplo" or "ondas")) throw Error("Modelo de cupom inválido.");
  foreach(var p in config.Impressoras)
  {
   var station=stations.SingleOrDefault(s=>s.Id==p.EstacaoId) ?? throw Error("A impressora não pertence a uma estação desta loja.");
   if(p.Id==Guid.Empty || string.IsNullOrWhiteSpace(p.Nome) || p.Nome.Length>100 || p.PapelMm is not(58 or 80) || p.TamanhoTexto is <11 or >24 || p.PaginaCodigo is not(860 or 850 or 858) || p.Endereco.Length>250 || (station.Tipo=="navegador" ? p.Conexao!="navegador" : p.Conexao is not("rede" or "sistema"))) throw Error("Configuração da impressora inválida.");
   if(p.Conexao=="rede")
   {
    var parts=p.Endereco.Split(':');
    if(parts.Length!=2 || !IPAddress.TryParse(parts[0],out var ip) || ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork || !int.TryParse(parts[1],out var port) || port is <1 or >65535) throw Error("Informe o IP e a porta da impressora, por exemplo 192.168.0.50:9100.");
   }
   if(p.Conexao=="sistema" && string.IsNullOrWhiteSpace(p.Endereco)) throw Error("Escolha a impressora instalada no Windows.");
  }
  foreach(var r in config.Regras)
   if(r.Id==Guid.Empty || string.IsNullOrWhiteSpace(r.Nome) || r.Nome.Length>100 || r.Gatilho is not("aceitar" or "manual") || r.Vias is <1 or >5 || r.ExpiraEmSegundos is <30 or >3600 || !config.Modelos.Any(m=>m.Id==r.ModeloId&&!m.Arquivado) || (r.ImpressoraId.HasValue&&!config.Impressoras.Any(p=>p.Id==r.ImpressoraId)) || r.Origens.Length==0 || r.Origens.Except(new[]{"ifood","cardapio_web","ia_whatsapp","atendente"}).Any() || r.Modalidades.Length==0 || r.Modalidades.Except(new[]{"entrega","retirada","local"}).Any()) throw Error("Regra de impressão inválida.");
  if(config.Automatica && !config.Regras.Any(r=>r.Ativa&&r.Gatilho=="aceitar"&&config.Impressoras.Any(p=>p.Id==r.ImpressoraId&&p.Ativa&&stations.Any(s=>s.Id==p.EstacaoId&&s.Ativa)))) throw Error("Escolha uma impressora ativa em uma regra ao aceitar antes de ligar a impressão automática.");
 }
 public async Task<PrintConfig> Save(Guid store,PrintConfig input)
 {
  var stations=await Stations(store); Validate(input,stations);
  await using var db=await source.OpenConnectionAsync(); await using var tx=await db.BeginTransactionAsync();
  var changed=await db.ExecuteAsync("UPDATE impressao_config SET automatica=@Automatica,revisao=revisao+1 WHERE estabelecimento_id=@store AND revisao=@Revisao",new{store,input.Automatica,input.Revisao},tx);
  if(changed==0) throw Error("A configuração mudou em outro computador. Recarregue antes de salvar.",409);
  // IDs cannot be borrowed from another store; ownership is checked before any write.
  foreach(var kind in new[]{"modelo","regra","impressora"})
  {
   var ids=kind=="modelo"?input.Modelos.Select(m=>m.Id).ToArray():kind=="regra"?input.Regras.Select(r=>r.Id).ToArray():input.Impressoras.Select(p=>p.Id).ToArray();
   if(await db.ExecuteScalarAsync<bool>($"SELECT EXISTS(SELECT 1 FROM impressao_{kind} WHERE id=ANY(@ids) AND estabelecimento_id<>@store)",new{ids,store},tx)) throw Error("Identificador não pertence a esta loja.",403);
   if(kind!="impressora") await db.ExecuteAsync($"DELETE FROM impressao_{kind} WHERE estabelecimento_id=@store AND NOT(id=ANY(@ids))",new{store,ids},tx);
  }
  foreach(var m in input.Modelos) await Write(db,tx,"modelo",store,m.Id,m);
  foreach(var r in input.Regras) await Write(db,tx,"regra",store,r.Id,r);
  foreach(var p in input.Impressoras)
  {
   // Telemetry only comes from the agent, never from configuration forms.
   var previous=await db.QuerySingleOrDefaultAsync<string>("SELECT dados::text FROM impressao_impressora WHERE id=@Id AND estabelecimento_id=@store",new{p.Id,store},tx);
   var old=previous==null?null:JsonSerializer.Deserialize<PrintPrinter>(previous,Json);
   p.UltimoEstado=old?.UltimoEstado??"desconhecido";p.UltimoEstadoEm=old?.UltimoEstadoEm;
   await db.ExecuteAsync("""
INSERT INTO impressao_impressora(id,estabelecimento_id,estacao_id,dados) VALUES(@Id,@store,@EstacaoId,CAST(@data AS jsonb))
ON CONFLICT(id) DO UPDATE SET dados=EXCLUDED.dados WHERE impressao_impressora.estabelecimento_id=@store AND impressao_impressora.estacao_id=@EstacaoId
""",new{p.Id,store,p.EstacaoId,data=Serialize(p)},tx);
  }
  await Event(db,tx,store,"print.station.updated",new{configuration=true});await tx.CommitAsync();return await Config(store);
 }
 public async Task<BusinessSettingsDto> Shop(Guid store)
 {
  await using var db=await source.OpenConnectionAsync();
  return await db.QuerySingleAsync<BusinessSettingsDto>("""
SELECT nome_fantasia AS NomeFantasia,cnpj_loja AS CnpjLoja,telefone AS Telefone,email AS Email,logradouro AS Logradouro,
numero AS Numero,bairro AS Bairro,cidade AS Cidade,uf AS Uf,url_logo AS UrlLogo,site_url AS SiteUrl,instagram_url AS InstagramUrl,facebook_url AS FacebookUrl
FROM estabelecimentos WHERE id=@store
""",new{store});
 }
 private const string StationSelect="""
SELECT e.id,e.nome,e.tipo,e.ativa,e.computador,e.ip_local AS IpLocal,e.versao_agente AS VersaoAgente,
e.ultimo_sinal_em AS UltimoSinalEm,s.sessao_id AS SessaoId,s.navegador
FROM impressao_estacao e LEFT JOIN impressao_estacao_sessao s ON s.estacao_id=e.id AND s.ultima_atividade>now()-interval '15 seconds'
WHERE e.estabelecimento_id=@store AND e.revogada_em IS NULL
""";
 public async Task<List<PrintStation>> Stations(Guid store)
 {await using var db=await source.OpenConnectionAsync();return (await db.QueryAsync<PrintStation>(StationSelect,new{store})).ToList();}
 public async Task<PrintStation> CreateStation(Guid store,int user,StationInput input)
 {
  if(input.Tipo is not("agente" or "navegador") || string.IsNullOrWhiteSpace(input.Nome)) throw Error("Informe nome e tipo da estação.");
  await using var db=await source.OpenConnectionAsync();var id=Guid.NewGuid();
  await db.ExecuteAsync("INSERT INTO impressao_estacao(id,estabelecimento_id,nome,tipo,criada_por) VALUES(@id,@store,@Nome,@Tipo,@user)",new{id,store,input.Nome,input.Tipo,user});
  return (await Stations(store)).Single(s=>s.Id==id);
 }
 public async Task SetStation(Guid store,Guid id,bool? active)
 {
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var n=await db.ExecuteAsync(active.HasValue?"UPDATE impressao_estacao SET ativa=@active WHERE id=@id AND estabelecimento_id=@store AND revogada_em IS NULL":"UPDATE impressao_estacao SET ativa=false,token_hash=NULL,revogada_em=now() WHERE id=@id AND estabelecimento_id=@store AND revogada_em IS NULL",new{id,store,active},tx);
  if(n==0) throw Error("Estação não encontrada.",404);
  await Event(db,tx,store,"print.station.updated",new{id});await tx.CommitAsync();
 }
 public async Task<object> PairCode(Guid store,int user,Guid station)
 {
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var valid=await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM impressao_estacao WHERE id=@station AND estabelecimento_id=@store AND tipo='agente' AND revogada_em IS NULL AND token_hash IS NULL)",new{store,station},tx);
  if(!valid) throw Error("Estação indisponível para pareamento.",409);
  await db.ExecuteAsync("DELETE FROM impressao_pareamento WHERE estacao_id=@station OR expira_em<now()",new{station},tx);
  for(var i=0;i<10;i++)
  {
   var code=RandomNumberGenerator.GetInt32(0,1_000_000).ToString("D6");var expires=DateTimeOffset.UtcNow.AddMinutes(5);
   if(await db.ExecuteAsync("INSERT INTO impressao_pareamento(codigo_hash,estabelecimento_id,estacao_id,criado_por,expira_em) VALUES(@hash,@store,@station,@user,@expires) ON CONFLICT DO NOTHING",new{hash=Hash(code),store,station,user,expires},tx)==0)continue;
   await tx.CommitAsync();return new{codigo=code,expiraEm=expires,estacaoId=station};
  }
  throw Error("Não foi possível gerar código. Tente novamente.",503);
 }
 public async Task<object> Pair(PairInput input)
 {
  if(input.Codigo.Length!=6 || !input.Codigo.All(char.IsAsciiDigit) || input.Computador.Length>100) throw Error("Código de pareamento inválido.");
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var pair=await db.QuerySingleOrDefaultAsync<AgentIdentity>("""
UPDATE impressao_pareamento SET usado_em=now() WHERE codigo_hash=@hash AND usado_em IS NULL AND expira_em>now()
RETURNING estabelecimento_id AS EstabelecimentoId,estacao_id AS Id
""",new{hash=Hash(input.Codigo)},tx) ?? throw Error("Código inválido ou expirado.",401);
  var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  if(await db.ExecuteAsync("UPDATE impressao_estacao SET token_hash=@hash,computador=@Computador WHERE id=@Id AND estabelecimento_id=@EstabelecimentoId AND revogada_em IS NULL AND token_hash IS NULL",new{pair.Id,pair.EstabelecimentoId,hash=Hash(token),input.Computador},tx)==0) throw Error("Estação revogada ou já pareada.",409);
  await Event(db,tx,pair.EstabelecimentoId,"print.station.updated",new{pair.Id});await tx.CommitAsync();return new{token,estacaoId=pair.Id};
 }
 public async Task<AgentIdentity?> Agent(string token)
 {
  if(token.Length!=64)return null;
  await using var db=await source.OpenConnectionAsync();return await db.QuerySingleOrDefaultAsync<AgentIdentity>("SELECT estabelecimento_id AS EstabelecimentoId,id FROM impressao_estacao WHERE token_hash=@hash AND tipo='agente' AND ativa AND revogada_em IS NULL",new{hash=Hash(token)});
 }
 public async Task<bool> Signal(Guid store,Guid station,int user,Heartbeat beat,bool agent=false)
 {
  if(beat.SessaoId==Guid.Empty || beat.Computador?.Length>100 || beat.Navegador?.Length>200 || beat.Versao?.Length>30 || beat.Impressoras?.Length>100 || beat.IpLocal?.Length>45) throw Error("Sinal de estação inválido.");
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var kind=await db.QuerySingleOrDefaultAsync<string>("SELECT tipo FROM impressao_estacao WHERE id=@station AND estabelecimento_id=@store AND ativa AND revogada_em IS NULL FOR UPDATE",new{store,station},tx);
  if(kind!=(agent?"agente":"navegador"))throw Error("Estação indisponível.",403);
  var owner=true;
  if(!agent) owner=await db.ExecuteAsync("""
INSERT INTO impressao_estacao_sessao(estabelecimento_id,estacao_id,sessao_id,usuario_id,navegador,ultima_atividade)
VALUES(@store,@station,@SessaoId,@user,@browser,now())
ON CONFLICT(estacao_id) DO UPDATE SET sessao_id=@SessaoId,usuario_id=@user,navegador=@browser,ultima_atividade=now()
WHERE impressao_estacao_sessao.sessao_id=@SessaoId OR impressao_estacao_sessao.ultima_atividade<now()-interval '15 seconds'
""",new{store,station,beat.SessaoId,user,browser=beat.Navegador??"Navegador"},tx)>0;
  if(owner)await db.ExecuteAsync("UPDATE impressao_estacao SET ultimo_sinal_em=now(),computador=COALESCE(@Computador,computador),ip_local=COALESCE(@IpLocal,ip_local),versao_agente=COALESCE(@Versao,versao_agente) WHERE id=@station AND estabelecimento_id=@store",new{store,station,beat.Computador,beat.Versao,beat.IpLocal},tx);
  if(agent && beat.Impressoras!=null)
  {
   foreach(var detected in beat.Impressoras.Where(p=>!string.IsNullOrWhiteSpace(p.Nome)&&p.Nome.Length<=250&&p.Conexao is "sistema" or "rede"))
   {
    var address=detected.Endereco??detected.Nome;
    var state=detected.Estado is "pronta" or "offline" or "erro" or "sem_papel" or "tampa_aberta"?detected.Estado:"desconhecido";
    var data=Serialize(new PrintPrinter{EstacaoId=station,Nome=detected.Nome,Endereco=address,Conexao=detected.Conexao,UltimoEstado=state,UltimoEstadoEm=DateTimeOffset.UtcNow});
    var existing=await db.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM impressao_impressora WHERE estabelecimento_id=@store AND estacao_id=@station AND dados->>'conexao'=@Conexao AND dados->>'endereco'=@address LIMIT 1",new{store,station,detected.Conexao,address},tx);
    if(existing.HasValue) await db.ExecuteAsync("UPDATE impressao_impressora SET dados=jsonb_set(jsonb_set(dados,'{ultimoEstado}',CAST(@state AS jsonb)),'{ultimoEstadoEm}',to_jsonb(now())) WHERE id=@id AND estabelecimento_id=@store",new{id=existing.Value,store,state=Serialize(state)},tx);
    else if(detected.Conexao=="sistema")
    {
     var printer=JsonSerializer.Deserialize<PrintPrinter>(data,Json)!;
     await db.ExecuteAsync("INSERT INTO impressao_impressora(id,estabelecimento_id,estacao_id,dados) VALUES(@Id,@store,@station,CAST(@data AS jsonb))",new{printer.Id,store,station,data},tx);
    }
   }
  }
  await Event(db,tx,store,"print.station.updated",new{id=station});await tx.CommitAsync();return owner;
 }
 public async Task<PrintDraft?> Draft(Guid store,int user)
 {await using var db=await source.OpenConnectionAsync();var row=await db.QuerySingleOrDefaultAsync<(string Passo,string Conteudo)?>("SELECT passo,conteudo::text FROM impressao_rascunho WHERE estabelecimento_id=@store AND usuario_id=@user",new{store,user});return row.HasValue?new(row.Value.Passo,JsonSerializer.Deserialize<JsonElement>(row.Value.Conteudo)):null;}
 public async Task SaveDraft(Guid store,int user,PrintDraft? draft)
 {
  await using var db=await source.OpenConnectionAsync();
  if(draft==null){await db.ExecuteAsync("DELETE FROM impressao_rascunho WHERE estabelecimento_id=@store AND usuario_id=@user",new{store,user});return;}
  var json=Serialize(draft.Conteudo);if(json.Length>250_000 || draft.Passo.Length>50)throw Error("Rascunho muito grande.");
  await db.ExecuteAsync("INSERT INTO impressao_rascunho(estabelecimento_id,usuario_id,passo,conteudo) VALUES(@store,@user,@Passo,CAST(@json AS jsonb)) ON CONFLICT(estabelecimento_id,usuario_id) DO UPDATE SET passo=@Passo,conteudo=CAST(@json AS jsonb),atualizado_em=now()",new{store,user,draft.Passo,json});
 }
 private const string JobSelect="""
SELECT id,pedido_id AS PedidoId,estacao_id AS EstacaoId,impressora_id AS ImpressoraId,estado,motivo,origem,
origem_disparo AS OrigemDisparo,evento,documento::text AS DocumentoJson,criado_em AS CriadoEm,entregue_em AS EntregueEm,
impresso_em AS ImpressoEm,expira_em AS ExpiraEm,descartado AS Descartado FROM impressao_job
""";
 public async Task<List<PrintJob>> Jobs(Guid store,int? order=null)
 {await using var db=await source.OpenConnectionAsync();return (await db.QueryAsync<PrintJob>(JobSelect+" WHERE estabelecimento_id=@store AND (@order IS NULL OR pedido_id=@order) ORDER BY criado_em DESC LIMIT 500",new{store,order})).ToList();}
 public async Task<object> Summary(Guid store)
 {
  await using var db=await source.OpenConnectionAsync();
  return await db.QuerySingleAsync("""
SELECT count(*) FILTER(WHERE estado='impresso' AND (criado_em AT TIME ZONE 'America/Sao_Paulo')::date=(now() AT TIME ZONE 'America/Sao_Paulo')::date) AS impressos,
count(*) FILTER(WHERE estado='enviado' AND (criado_em AT TIME ZONE 'America/Sao_Paulo')::date=(now() AT TIME ZONE 'America/Sao_Paulo')::date) AS enviados,
count(*) FILTER(WHERE estado IN('falhou','expirado') AND (criado_em AT TIME ZONE 'America/Sao_Paulo')::date=(now() AT TIME ZONE 'America/Sao_Paulo')::date) AS falhas,
count(*) FILTER(WHERE estado IN('pendente','entregue')) AS fila,
count(DISTINCT pedido_id) FILTER(WHERE estado IN('falhou','expirado') AND NOT descartado) AS "naoImpressos"
FROM impressao_job WHERE estabelecimento_id=@store
""",new{store});
 }
 public async Task<object> Counts(Guid store)
 {
  await using var db=await source.OpenConnectionAsync();
  return await db.QueryAsync("""
SELECT 'regra' AS tipo,regra_id AS id,count(*) FILTER(WHERE estado='enviado') AS enviados,count(*) FILTER(WHERE estado='impresso') AS impressos FROM impressao_job
WHERE estabelecimento_id=@store AND regra_id IS NOT NULL AND (criado_em AT TIME ZONE 'America/Sao_Paulo')::date=(now() AT TIME ZONE 'America/Sao_Paulo')::date GROUP BY regra_id
UNION ALL
SELECT 'impressora',impressora_id,count(*) FILTER(WHERE estado='enviado'),count(*) FILTER(WHERE estado='impresso') FROM impressao_job
WHERE estabelecimento_id=@store AND impressora_id IS NOT NULL AND (criado_em AT TIME ZONE 'America/Sao_Paulo')::date=(now() AT TIME ZONE 'America/Sao_Paulo')::date GROUP BY impressora_id
""",new{store});
 }
 public async Task<List<PrintJob>> Pending(Guid store,Guid station,Guid session,bool agent)
 {
  await using var db=await source.OpenConnectionAsync();
  var valid=await db.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM impressao_estacao e WHERE e.estabelecimento_id=@store AND e.id=@station AND e.ativa AND e.revogada_em IS NULL
AND e.tipo=@kind AND (@agent OR EXISTS(SELECT 1 FROM impressao_estacao_sessao s WHERE s.estacao_id=e.id AND s.sessao_id=@session AND s.ultima_atividade>now()-interval '15 seconds')))
""",new{store,station,session,agent,kind=agent?"agente":"navegador"});
  if(!valid)throw Error("Esta aba não é a dona da estação.",409);
  return (await db.QueryAsync<PrintJob>(JobSelect+" WHERE estabelecimento_id=@store AND estacao_id=@station AND estado='pendente' AND expira_em>now() ORDER BY criado_em LIMIT 10",new{store,station})).ToList();
 }
 public async Task<bool> Receive(Guid store,Guid station,Guid job,Guid session,bool agent)
 {
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var changed=await db.ExecuteAsync("""
UPDATE impressao_job j SET estado='entregue',entregue_em=now(),sessao_id=@session
WHERE j.id=@job AND j.estabelecimento_id=@store AND j.estacao_id=@station AND j.estado='pendente' AND j.expira_em>now()
AND EXISTS(SELECT 1 FROM impressao_estacao e WHERE e.id=@station AND e.estabelecimento_id=@store AND e.ativa AND e.revogada_em IS NULL AND e.tipo=@kind
AND (@agent OR EXISTS(SELECT 1 FROM impressao_estacao_sessao s WHERE s.estacao_id=e.id AND s.sessao_id=@session AND s.ultima_atividade>now()-interval '15 seconds')))
""",new{store,station,job,session,agent,kind=agent?"agente":"navegador"},tx);
  if(changed>0)await Event(db,tx,store,"print.job.updated",new{id=job});await tx.CommitAsync();return changed>0;
 }
 public async Task Result(Guid store,Guid station,Guid job,PrintResult input,bool agent)
 {
  // RAW spooler/TCP are only evidence of sending. No caller may fabricate paper confirmation.
  if(input.Estado is not("enviado" or "falhou") || input.Motivo?.Length>500 || input.SessaoId==null)throw Error("Resultado de impressão inválido.");
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var changed=await db.ExecuteAsync("""
UPDATE impressao_job SET estado=@Estado,motivo=@Motivo,impresso_em=CASE WHEN @Estado='enviado' THEN now() ELSE NULL END
WHERE id=@job AND estabelecimento_id=@store AND estacao_id=@station AND sessao_id=@SessaoId AND estado IN('entregue','expirado')
AND EXISTS(SELECT 1 FROM impressao_estacao WHERE id=@station AND estabelecimento_id=@store AND ativa AND revogada_em IS NULL AND tipo=@kind)
""",new{store,station,job,input.Estado,Motivo=input.Estado=="falhou"?input.Motivo??"Não foi possível imprimir.":null,input.SessaoId,kind=agent?"agente":"navegador"},tx);
  if(changed==0 && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM impressao_job WHERE id=@job AND estabelecimento_id=@store AND estacao_id=@station AND sessao_id=@SessaoId AND estado=@Estado)",new{job,store,station,input.SessaoId,input.Estado},tx))throw Error("Trabalho não está reservado para esta estação.",409);
  if(changed>0)await Event(db,tx,store,"print.job.updated",new{id=job});await tx.CommitAsync();
 }
 internal static async Task InsertJob(NpgsqlConnection db,NpgsqlTransaction tx,Guid store,PrintDocument? document,PrintRule? rule,string key,string trigger,int? user,string? reason=null,string? origin=null,int? pedido=null,int copy=1,DateTimeOffset? acceptedAt=null)
 {
  var id=Guid.NewGuid();var printer=document?.Impressora;
  var expires=(acceptedAt??DateTimeOffset.UtcNow).AddSeconds(rule?.ExpiraEmSegundos??300);
  var expired=reason==null&&expires<=DateTimeOffset.UtcNow;
  var n=await db.ExecuteAsync("""
INSERT INTO impressao_job(id,estabelecimento_id,pedido_id,regra_id,impressora_id,estacao_id,via,evento,chave_unica,documento,estado,motivo,origem_disparo,origem,usuario_id,expira_em)
VALUES(@id,@store,@pedido,@ruleId,@printerId,@station,@copy,@ev,@key,CAST(@doc AS jsonb),@state,@reason,@trigger,@origin,@user,@expires) ON CONFLICT(chave_unica) DO NOTHING
""",new{id,store,pedido=document?.PedidoId??pedido,ruleId=rule?.Id,printerId=printer?.Id,station=printer?.EstacaoId,copy,ev=trigger=="automatico"?"aceitar":trigger,key,doc=document==null?null:Serialize(document),state=expired?"expirado":reason==null?"pendente":"ignorado",reason=expired?"O prazo para imprimir este aceite esgotou. Confira o pedido antes de reimprimir.":reason,trigger,origin=document?.Origem??origin??"atendente",user,expires},tx);
  if(n>0)await Event(db,tx,store,"print.job.created",new{id});
 }
 public async Task Manual(Guid store,int user,int? orderId,Guid? printerId,bool test,string requestId)
 {
  if(!Guid.TryParse(requestId,out _))throw Error("Informe uma chave de idempotência válida.");
  var config=await Config(store,true);var shop=await Shop(store);var stations=await Stations(store);
  var order=test?new APIBack.DTOs.Delivery.PedidoDetalheDto{Id=0,Origem="atendente",NomeCliente="Impressão de teste",CriadoEm=DateTime.UtcNow,Itens=[new(){Nome="Teste de acentos: ação, café, pão, ç",Quantidade=1}],Total=0}:await orders.GetAsync(store,orderId??0)??throw Error("Pedido não encontrado.",404);
  if(order.Origem=="simulador")throw Error("O simulador não imprime em impressoras reais.");
  var manualRules=config.Regras.Where(r=>r.Ativa&&r.Gatilho=="manual").ToArray();
  var rules=(manualRules.Length>0?manualRules:config.Regras.Where(r=>r.Ativa&&r.Gatilho=="aceitar"))
   .Where(r=>r.Origens.Contains(order.Origem)&&r.Modalidades.Contains(PrintDocumentBuilder.Modality(order)))
   .OrderBy(r=>r.Ordem).ToArray();
  var targets=new List<(PrintPrinter Printer,PrintRule? Rule)>();
  if(printerId.HasValue||test||rules.Length==0&&manualRules.Length==0)
  {
   var printer=config.Impressoras.FirstOrDefault(p=>p.Ativa&&(!printerId.HasValue||p.Id==printerId))??throw Error("Configure uma impressora ativa em Impressão.");
   targets.Add((printer,config.Regras.OrderBy(r=>r.Ordem).FirstOrDefault(r=>r.Ativa&&r.ImpressoraId==printer.Id)));
  }
  else foreach(var rule in rules)
  {
   var printer=config.Impressoras.SingleOrDefault(p=>p.Id==rule.ImpressoraId&&p.Ativa)??throw Error("A regra manual não tem impressora ativa.");targets.Add((printer,rule));
  }
  if(targets.Count==0)throw Error("Nenhuma regra manual imprime esta origem e modalidade.");
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  foreach(var target in targets)
  {
   if(!stations.Any(s=>s.Id==target.Printer.EstacaoId&&s.Ativa))throw Error("A estação está desativada.");
   var model=config.Modelos.FirstOrDefault(m=>!m.Arquivado&&m.Id==target.Rule?.ModeloId)??config.Modelos.First(m=>!m.Arquivado);
   var doc=PrintDocumentBuilder.Build(order,shop,model,target.Printer,!test) with{PedidoId=test?null:order.Id};
   for(var copy=1;copy<=(test?1:target.Rule?.Vias??1);copy++)
    await InsertJob(db,tx,store,doc,target.Rule,$"manual:{store}:{requestId}:{target.Printer.Id}:{target.Rule?.Id}:{copy}",test?"teste":"manual",user,copy:copy);
  }
  await tx.CommitAsync();
 }
 public async Task JobAction(Guid store,int user,Guid id,string action,string? requestId)
 {
  await using var db=await source.OpenConnectionAsync();await using var tx=await db.BeginTransactionAsync();
  var job=await db.QuerySingleOrDefaultAsync<PrintJob>(JobSelect+" WHERE id=@id AND estabelecimento_id=@store FOR UPDATE",new{id,store},tx)??throw Error("Trabalho não encontrado.",404);
  if(action=="cancelar")
  {if(job.Estado!="pendente")throw Error("Somente trabalhos na fila podem ser cancelados.",409);await db.ExecuteAsync("UPDATE impressao_job SET estado='cancelado',descartado=true WHERE id=@id AND estabelecimento_id=@store",new{id,store},tx);}
  else if(action=="descartar")await db.ExecuteAsync("UPDATE impressao_job SET descartado=true WHERE id=@id AND estabelecimento_id=@store",new{id,store},tx);
  else if(action is "tentar-novamente" or "reimprimir")
  {
   if(!Guid.TryParse(requestId,out _))throw Error("Chave de idempotência inválida.");
   if(action=="tentar-novamente" && job.Estado is not("falhou" or "expirado"))throw Error("Só é possível tentar novamente um trabalho que falhou ou expirou.",409);
   var config=await Config(store);var printer=config.Impressoras.SingleOrDefault(p=>p.Id==job.ImpressoraId&&p.Ativa)??throw Error("A impressora foi desativada.");
   if(!(await Stations(store)).Any(s=>s.Id==printer.EstacaoId&&s.Ativa))throw Error("A estação foi desativada.");
   var original=job.Documento;
   if(original?.Origem=="ifood")
   {
    var current=job.PedidoId.HasValue?await orders.GetAsync(store,job.PedidoId.Value):null;
    if(current==null)throw Error("Pedido iFood não encontrado.",404);
    original=PrintDocumentBuilder.Build(current,await Shop(store),original.Modelo,printer);
   }
   if(original==null)
   {
    var order=job.PedidoId.HasValue?await orders.GetAsync(store,job.PedidoId.Value):null;
    if(order==null)throw Error("O pedido deste trabalho não foi encontrado.",404);
    var model=config.Modelos.FirstOrDefault(m=>!m.Arquivado)??throw Error("Configure um modelo de cupom.");
    original=PrintDocumentBuilder.Build(order,await Shop(store),model,printer);
   }
   var doc=original with{Reimpressao=true,Impressora=printer,EmitidoEm=DateTimeOffset.UtcNow};
   await InsertJob(db,tx,store,doc,null,$"retry:{store}:{requestId}","manual",user);
   // Original warning is cleared only when replacement succeeds, or explicitly dismissed.
  }
  else throw Error("Ação inválida.");
  await Event(db,tx,store,"print.job.updated",new{id});await tx.CommitAsync();
 }
}
