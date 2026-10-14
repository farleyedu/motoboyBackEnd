using APIBack.DTOs.Delivery;
using APIBack.Service;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Commands;

public sealed class IfoodCommandService
{
    private readonly IfoodCommandRepository _repository;
    private readonly IIfoodCommandClient _client;
    private readonly IfoodOptions _options;
    public IfoodCommandService(IfoodCommandRepository repository,IIfoodCommandClient client,IOptions<IfoodOptions> options)
        => (_repository,_client,_options)=(repository,client,options.Value);
    public Task<IfoodCommandDto> SubmitAsync(Guid store,int user,int pedido,IfoodCommandRequest request,CancellationToken ct=default)
    {
        if(!_options.Enabled || !_options.CommandsEnabled) throw new DeliveryDomainException(503,"IFOOD_COMMANDS_DISABLED","Os comandos iFood ainda não estão habilitados.");
        return _repository.EnqueueAsync(store,user,pedido,request,ct);
    }
    public Task<IfoodCommandDto?> ReadAsync(Guid store,Guid operation,CancellationToken ct=default) => _repository.ReadAsync(store,operation,ct);
    public Task<IfoodCapabilitiesDto> CapabilitiesAsync(Guid store,int pedido,CancellationToken ct=default) => _repository.CapabilitiesAsync(store,pedido,ct);
    public async Task<int> RunOnceAsync(CancellationToken ct=default)
    {
        if(!_options.Enabled || !_options.CommandsEnabled) return 0;
        var count=0;
        for(var index=0;index<_options.CommandBatchSize;index++)
        {
            var item=await _repository.ClaimAsync(ct);if(item==null) break;
            try
            {
                var response=await _client.SendAsync(item,token=>_repository.MarkSendingAsync(item,token),ct);
                await _repository.CompleteAsync(item,response,ct);count++;
            }
            catch(DeliveryDomainException ex) { await _repository.FailBeforeSendAsync(item,ex.Code,false,ct); }
            catch(IfoodIntegrationException ex) { await _repository.FailBeforeSendAsync(item,ex.Code,ex.Retryable,ct); }
            // Shutdown/crash: recuperação da reserva diferencia envio iniciado de falha anterior ao HTTP.
        }
        return count;
    }
}
public sealed class IfoodCommandWorker : BackgroundService
{
    private readonly IfoodCommandService _service;
    private readonly IfoodOptions _options;
    private readonly ILogger<IfoodCommandWorker> _logger;
    public IfoodCommandWorker(IfoodCommandService service,IOptions<IfoodOptions> options,ILogger<IfoodCommandWorker> logger)
        => (_service,_options,_logger)=(service,options.Value,logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!_options.Enabled || !_options.CommandsEnabled) return;
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await _service.RunOnceAsync(stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(IfoodIntegrationException ex) { _logger.LogWarning("Comandos iFood interrompidos: {Code}.",ex.Code); }
            catch(Exception) { _logger.LogError("Comandos iFood interrompidos: IFOOD_COMMAND_INTERNAL_ERROR."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
