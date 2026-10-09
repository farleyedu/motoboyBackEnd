using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;
using Npgsql;
using System.Text.Json;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    private sealed class CompletionOrder
    {
        public int PedidoId { get; set; }
        public long StopId { get; set; }
        public string? NomeCliente { get; set; }
        public decimal? Total { get; set; }
        public string? PaymentStatus { get; set; }
        public string? Code { get; set; }
        public bool RequiresCode { get; set; }
        public bool RequiresProof { get; set; }
        public DateTimeOffset? PickedUp { get; set; }
        public DateTimeOffset? Arrived { get; set; }
    }
    private static Task<CompletionOrder?> ReadCompletionOrder(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int rider, int pedido, CancellationToken ct) => c.QuerySingleOrDefaultAsync<CompletionOrder>(new CommandDefinition($"""
SELECT s.id AS StopId, p.id AS PedidoId,p.nome_cliente AS NomeCliente,
 CASE WHEN p.value::text ~ {NumericPattern} THEN p.value::text::numeric END AS Total,
 CASE WHEN LOWER(p.tipo_pagamento::text)='pagoapp' THEN 'pago' ELSE p.status_pagamento::text END AS PaymentStatus,p.codigo_entrega::text AS Code,
 COALESCE(d.require_delivery_code,false) AS RequiresCode,COALESCE(d.require_delivery_proof,false) AS RequiresProof,
 s.picked_up_at_utc AS PickedUp,s.arrived_at_utc AS Arrived
 FROM delivery_route_stops s JOIN pedido p ON p.id=s.pedido_id
 LEFT JOIN delivery_settings d ON d.estabelecimento_id=s.estabelecimento_id
 WHERE s.estabelecimento_id=@Store AND s.motoboy_id=@Rider AND s.pedido_id=@Pedido AND s.stop_status='en_route'
 FOR UPDATE OF s,p
""", new { Store=store,Rider=rider,Pedido=pedido }, tx, commandTimeout:10,cancellationToken:ct));
    private static CompletionOrder RequireCompletionOrder(CompletionOrder? row) => row ?? throw new DeliveryDomainException(409,"CURRENT_DELIVERY_CHANGED","Este pedido não é mais sua entrega atual. Confira a rota.");
    private static void CheckCompletionCode(CompletionOrder order,string? code)
    {
        if (order.RequiresCode && DeliveryRules.HasDeliveryCode(order.Code) && !DeliveryRules.DeliveryCodeMatches(order.Code,code))
            throw new DeliveryDomainException(422,"DELIVERY_CODE_INVALID","O código não confere. Peça ao cliente no momento da entrega.");
    }
    private async Task<(NpgsqlConnection Connection,NpgsqlTransaction Transaction)> OpenCompletionTransaction(CancellationToken ct)
    {
        var c=await _dataSource.OpenConnectionAsync(ct);
        try { var tx=await c.BeginTransactionAsync(ct); await c.ExecuteAsync(new CommandDefinition("SET LOCAL statement_timeout='10s'",transaction:tx,cancellationToken:ct)); return(c,tx); }
        catch { await c.DisposeAsync(); throw; }
    }
    public async Task<DeliveryCompletionContext> GetCompletionContextAsync(Guid store,int rider,int pedido,CancellationToken ct)
    {
        var opened=await OpenCompletionTransaction(ct); await using var c=opened.Connection; await using var tx=opened.Transaction;
        var version=await LockQueuesAsync(c,tx,store,rider);
        var row=RequireCompletionOrder(await ReadCompletionOrder(c,tx,store,rider,pedido,ct));
        var result=new DeliveryCompletionContext { PedidoId=pedido,Version=version,NomeCliente=row.NomeCliente,Total=row.Total,RequiresCode=row.RequiresCode && DeliveryRules.HasDeliveryCode(row.Code),RequiresProof=row.RequiresProof,RequiresPayment=!DeliveryCompletionRules.IsPaid(row.PaymentStatus) };
        result.Checklist = await ReadChecklistAsync(c, tx, store, pedido, ct);
        await tx.CommitAsync(ct); return result;
    }
    public async Task<bool> ValidateCompletionCodeAsync(Guid store,int rider,int pedido,string code,CancellationToken ct)
    {
        if (code==null || code.Length>32) throw new DeliveryDomainException(422,"DELIVERY_CODE_INVALID","Código inválido.");
        var opened=await OpenCompletionTransaction(ct); await using var c=opened.Connection; await using var tx=opened.Transaction;
        await LockQueuesAsync(c,tx,store,rider); var row=RequireCompletionOrder(await ReadCompletionOrder(c,tx,store,rider,pedido,ct));
        CheckCompletionCode(row,code); await tx.CommitAsync(ct); return true;
    }
    public async Task<Guid> SaveCompletionProofAsync(Guid store,int rider,int pedido,string base64,CancellationToken ct)
    {
        var image=MotoboyContaService.SanitizeImage(base64,false);
        var opened=await OpenCompletionTransaction(ct); await using var c=opened.Connection; await using var tx=opened.Transaction;
        await LockQueuesAsync(c,tx,store,rider); var row=RequireCompletionOrder(await ReadCompletionOrder(c,tx,store,rider,pedido,ct));
        var id=await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
INSERT INTO delivery_completion_proofs(id,estabelecimento_id,motoboy_id,pedido_id,stop_id,conteudo)
 VALUES(@Id,@Store,@Rider,@Pedido,@Stop,@Image)
 ON CONFLICT(estabelecimento_id,motoboy_id,stop_id) DO UPDATE SET conteudo=EXCLUDED.conteudo,created_at_utc=NOW()
 RETURNING id
""",new { Id=Guid.NewGuid(),Store=store,Rider=rider,Pedido=pedido,Stop=row.StopId,Image=image },tx,commandTimeout:10,cancellationToken:ct));
        await tx.CommitAsync(ct); return id;
    }
    public async Task<string?> ReadPendingCompletionProofAsync(Guid store,int rider,int pedido,CancellationToken ct)
    {
        var opened=await OpenCompletionTransaction(ct); await using var c=opened.Connection; await using var tx=opened.Transaction;
        await LockQueuesAsync(c,tx,store,rider);
        var row=RequireCompletionOrder(await ReadCompletionOrder(c,tx,store,rider,pedido,ct));
        var bytes=await c.ExecuteScalarAsync<byte[]?>(new CommandDefinition("SELECT conteudo FROM delivery_completion_proofs WHERE estabelecimento_id=@Store AND motoboy_id=@Rider AND stop_id=@Stop",new {Store=store,Rider=rider,Stop=row.StopId},tx,commandTimeout:10,cancellationToken:ct));
        await tx.CommitAsync(ct);
        return bytes==null?null:"data:image/jpeg;base64,"+Convert.ToBase64String(bytes);
    }
    public async Task<DeliveryCompletionResult> CompleteDeliveryAsync(Guid store,int rider,int user,Guid session,long epoch,DeliveryCompletionRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty) throw new DeliveryDomainException(422,"OPERATION_ID_REQUIRED","Identificação da conclusão ausente.");
        var opened=await OpenCompletionTransaction(ct); await using var c=opened.Connection; await using var tx=opened.Transaction;
        var version=await LockQueuesAsync(c,tx,store,rider);
        var previous=await c.QuerySingleOrDefaultAsync<(string Hash,string Receipt)>(new CommandDefinition("SELECT payload_hash AS Hash,receipt::text AS Receipt FROM delivery_completions WHERE operation_id=@Operation AND estabelecimento_id=@Store AND motoboy_id=@Rider AND user_id=@User",new { Operation=request.OperationId,Store=store,Rider=rider,User=user },tx,commandTimeout:10,cancellationToken:ct));
        var hash=DeliveryCompletionRules.Hash(request);
        if(previous.Receipt!=null)
        {
            if(previous.Hash!=hash) throw new DeliveryDomainException(409,"IDEMPOTENCY_CONFLICT","Esta identificação já foi usada com outra conferência.");
            var replay=new DeliveryCompletionResult { Receipt=JsonSerializer.Deserialize<DeliveryReceipt>(previous.Receipt)!,Queue=await BuildSnapshotAsync(c,tx,store,rider,version,ct) };
            await tx.CommitAsync(ct); return replay;
        }
        if(version!=request.ExpectedVersion) throw new DeliveryDomainException(409,"QUEUE_VERSION_CONFLICT","A rota mudou. Recarregue e confira a entrega antes de concluir.");
        var row=RequireCompletionOrder(await ReadCompletionOrder(c,tx,store,rider,request.ExpectedPedidoId,ct));
        if(row.PickedUp==null || row.Arrived==null) throw new DeliveryDomainException(422,"DELIVERY_MILESTONES_REQUIRED","Confirme a retirada e a chegada antes de concluir.");
        // Sessão e usuário revalidados sob a mesma transação que grava o recibo.
        var valid=await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM motoboy_active_sessions s WHERE s.session_id=@Session AND s.session_epoch=@Epoch
 AND s.motoboy_id=@Rider AND s.id_estabelecimento=@Store AND s.id_usuario=@User AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc>NOW())
""",new { Session=session,Epoch=epoch,Rider=rider,Store=store,User=user },tx,commandTimeout:10,cancellationToken:ct));
        if(!valid) throw new DeliveryDomainException(409,"SESSION_CHANGED","Recupere seu turno antes de concluir.");
        CheckCompletionCode(row,request.Codigo);
        var paid=DeliveryCompletionRules.IsPaid(row.PaymentStatus);
        var manifest = await ReadChecklistAsync(c, tx, store, row.PedidoId, ct);
        DeliveryChecklistRules.Validate(row.PedidoId, manifest, request.Checklist);
        if (request.Checklist != null)
            await SaveChecklistAsync(c, tx, store, rider, row.StopId, "delivery", manifest, request.Checklist, ct);
        DeliveryCompletionRules.Validate(request,row.Total,!paid);
        if(row.RequiresProof && !request.ProofId.HasValue) throw new DeliveryDomainException(422,"PROOF_REQUIRED","Anexe o comprovante solicitado pela loja.");
        if(request.ProofId.HasValue && !await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_completion_proofs WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider AND stop_id=@Stop)",new { Id=request.ProofId,Store=store,Rider=rider,Stop=row.StopId },tx,commandTimeout:10,cancellationToken:ct)))
            throw new DeliveryDomainException(422,"PROOF_INVALID","Este comprovante não pertence à entrega atual.");
        if(!paid) await c.ExecuteAsync(new CommandDefinition("UPDATE pedido SET status_pagamento='pago',tipo_pagamento=@Method WHERE id=@Pedido",new { Pedido=row.PedidoId,Method=string.Join(" + ",request.Payments.Select(p=>p.Method)) },tx,commandTimeout:10,cancellationToken:ct));
        var queue=await CompleteCurrentInternalAsync(c,tx,store,rider,"motoboy",request.Codigo,true);
        var receipt=new DeliveryReceipt { OperationId=request.OperationId,PedidoId=row.PedidoId,NomeCliente=row.NomeCliente,CompletedAtUtc=DateTimeOffset.UtcNow,Total=row.Total,CodeChecked=row.RequiresCode&&DeliveryRules.HasDeliveryCode(row.Code),PaidBeforeDelivery=paid,Payments=request.Payments,ProofId=request.ProofId,Checklist=request.Checklist };
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_completions(operation_id,estabelecimento_id,motoboy_id,user_id,session_id,session_epoch,pedido_id,stop_id,payload_hash,receipt)
 VALUES(@Operation,@Store,@Rider,@User,@Session,@Epoch,@Pedido,@Stop,@Hash,CAST(@Receipt AS jsonb))
""",new { Operation=request.OperationId,Store=store,Rider=rider,User=user,Session=session,Epoch=epoch,Pedido=row.PedidoId,Stop=row.StopId,Hash=hash,Receipt=JsonSerializer.Serialize(receipt) },tx,commandTimeout:10,cancellationToken:ct));
        if(await MotoboyWorkService.Available(c,tx,ct))
        {
            var cash=paid?0:request.Payments.Where(p=>string.Equals(p.Method,"dinheiro",StringComparison.OrdinalIgnoreCase)).Sum(p=>p.Amount);
            await c.ExecuteAsync(new CommandDefinition("UPDATE delivery_rider_work_entries SET store_cash=@Cash,cash_confirmed=TRUE WHERE stop_id=@Stop AND estabelecimento_id=@Store AND motoboy_id=@Rider",new { Cash=cash,Stop=row.StopId,Store=store,Rider=rider },tx,cancellationToken:ct));
        }
        await tx.CommitAsync(ct); return new DeliveryCompletionResult { Receipt=receipt,Queue=queue };
    }
    public async Task<DeliveryReceipt?> GetDeliveryReceiptAsync(int user,Guid operation,CancellationToken ct)
    {
        await using var c=await _dataSource.OpenConnectionAsync(ct);
        var json=await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT receipt::text FROM delivery_completions WHERE user_id=@User AND operation_id=@Operation",new { User=user,Operation=operation },commandTimeout:10,cancellationToken:ct));
        return json==null?null:JsonSerializer.Deserialize<DeliveryReceipt>(json);
    }
    public async Task<string?> GetDeliveryProofAsync(int user,Guid operation,CancellationToken ct)
    {
        await using var c=await _dataSource.OpenConnectionAsync(ct);
        var bytes=await c.ExecuteScalarAsync<byte[]?>(new CommandDefinition("SELECT p.conteudo FROM delivery_completions d JOIN delivery_completion_proofs p ON p.id=(d.receipt->>'ProofId')::uuid WHERE d.user_id=@User AND d.operation_id=@Operation",new { User=user,Operation=operation },commandTimeout:10,cancellationToken:ct));
        return bytes==null?null:"data:image/jpeg;base64,"+Convert.ToBase64String(bytes);
    }
}
