using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Service;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Exceptions;

public sealed class IfoodExceptionService
{
    private readonly IfoodCommandRepository _repository;
    private readonly IIfoodCommandClient _client;
    private readonly IIfoodCancellationReasonsClient _reasons;
    private readonly IOptionsMonitor<IfoodOptions> _optionsMonitor;
    private IfoodOptions _options => _optionsMonitor.CurrentValue;
    private readonly TimeProvider _clock;
    public IfoodExceptionService(IfoodCommandRepository repository,IIfoodCommandClient client,IIfoodCancellationReasonsClient reasons,IOptionsMonitor<IfoodOptions> options,TimeProvider clock)
        => (_repository,_client,_reasons,_optionsMonitor,_clock)=(repository,client,reasons,options,clock);
    private void Enabled()
    {
        if(!_options.Enabled || !_options.ExceptionsEnabled) throw new DeliveryDomainException(503,"IFOOD_EXCEPTIONS_DISABLED","Cancelamento e respostas a disputas iFood ainda não estão habilitados.");
    }
    public async Task<IfoodCancellationReasonsDto> ReasonsAsync(Guid store,int pedido,CancellationToken ct=default)
    {
        Enabled();var order=await _repository.CancellationContextAsync(store,pedido,ct:ct);
        var reasons=await _reasons.GetAsync(order,async token=>{await _repository.CancellationContextAsync(store,pedido,order.Revision,token);},ct);
        return await _repository.StoreReasonsAsync(order,reasons,ct);
    }
    public Task<IfoodCommandDto> CancelAsync(Guid store,int user,int pedido,IfoodCancellationRequest request,CancellationToken ct=default)
    { Enabled();return _repository.EnqueueCancellationAsync(store,user,pedido,request,ct); }
    public Task<IfoodDisputeOptionsDto> OptionsAsync(Guid store,int pedido,Guid dispute,CancellationToken ct=default)
    { Enabled();return _repository.DisputeOptionsAsync(store,pedido,dispute,ct); }
    public Task<IfoodCommandDto> RespondAsync(Guid store,int user,int pedido,Guid dispute,IfoodDisputeResponseRequest request,CancellationToken ct=default)
    { Enabled();return _repository.EnqueueDisputeAsync(store,user,pedido,dispute,request,ct); }
    public async Task<int> RunOnceAsync(CancellationToken ct=default)
    {
        if(!_options.Enabled || !_options.ExceptionsEnabled) return 0;
        var count=0;
        for(var index=0;index<_options.ExceptionBatchSize;index++)
        {
            var item=await _repository.ClaimAsync(ct,_options.AllowedExceptionActions.ToArray(),_options.ExceptionMerchants.Select(m=>m.MerchantId).ToArray());
            if(item==null) break;
            try
            {
                IReadOnlyList<IfoodCancellationReasonDto>? fresh=null;DateTimeOffset? fetched=null;
                if(item.Command=="request_cancellation")
                {
                    var order=await _repository.RenewExceptionLookupAsync(item,ct);
                    fresh=await _reasons.GetAsync(order,async token=>{await _repository.RenewExceptionLookupAsync(item,token);},ct);fetched=_clock.GetUtcNow();
                }
                var response=await _client.SendAsync(item,token=>_repository.MarkExceptionSendingAsync(item,fresh,fetched,token),ct);
                await _repository.CompleteAsync(item,response,ct);count++;
            }
            catch(DeliveryDomainException ex) { await _repository.FailBeforeSendAsync(item,ex.Code,false,ct); }
            catch(IfoodIntegrationException ex) { await _repository.FailBeforeSendAsync(item,ex.Code,ex.Retryable,ct,ex.RetryAfter); }
        }
        return count;
    }
}
public sealed class IfoodExceptionWorker : BackgroundService
{
    private readonly IfoodExceptionService _service;
    private readonly IOptionsMonitor<IfoodOptions> _options;
    private readonly ILogger<IfoodExceptionWorker> _logger;
    public IfoodExceptionWorker(IfoodExceptionService service,IOptionsMonitor<IfoodOptions> options,ILogger<IfoodExceptionWorker> logger)=>(_service,_options,_logger)=(service,options,logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            if(!_options.CurrentValue.Enabled || !_options.CurrentValue.ExceptionsEnabled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken); } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
                continue;
            }
            try { await _service.RunOnceAsync(stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(IfoodIntegrationException ex) { _logger.LogWarning("Cancelamento/disputas iFood interrompidos: {Code}.",ex.Code); }
            catch(Exception) { _logger.LogError("Cancelamento/disputas iFood interrompidos: IFOOD_EXCEPTION_INTERNAL_ERROR."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
