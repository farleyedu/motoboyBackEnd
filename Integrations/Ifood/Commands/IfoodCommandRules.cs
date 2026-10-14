using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;

namespace APIBack.Integrations.Ifood.Commands;

public sealed class IfoodCommandContext
{
    public int PedidoId { get; set; } public Guid MerchantId { get; set; } public Guid ExternalOrderId { get; set; }
    public Guid EstabelecimentoId { get; set; } public long Revision { get; set; }
    public string? RemoteStatus { get; set; } public string PreparationState { get; set; } = "";
    public string SynchronizationState { get; set; } = ""; public string CancellationState { get; set; } = "";
    public bool DataIsCurrent { get; set; }
    public bool DeliveryRoutingRequired { get; set; }
    public bool DeliveryRouteAccepted { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Payload { get; set; } = "{}";
    public override string ToString() => "[contexto de comando iFood; payload omitido]";
}
public sealed record IfoodCommandWindow(DateTimeOffset NotBeforeUtc,DateTimeOffset? DeadlineAtUtc);

public static class IfoodCommandRules
{
    public static bool IsKnown(string command) => command is "confirm" or "start_preparation" or "ready_to_pickup" or "dispatch";
    public static string Endpoint(string command) => command switch
    { "confirm"=>"confirm","start_preparation"=>"startPreparation","ready_to_pickup"=>"readyToPickup","dispatch"=>"dispatch",_=>throw Error("IFOOD_COMMAND_UNKNOWN") };
    public static string Payload(string command) => command == "dispatch" ? "{\"deliveredBy\":\"MERCHANT\"}" : "{}";
    public static IfoodCommandWindow Validate(string command,IfoodCommandContext context,long revision,DateTimeOffset now,IfoodOptions options)
    {
        if(!options.Enabled || !options.CommandsEnabled) throw Error("IFOOD_COMMANDS_DISABLED");
        if(!IsKnown(command) || !options.AllowedCommands.Contains(command)) throw Error("IFOOD_COMMAND_NOT_ENABLED");
        if(!options.CommandMerchants.Any(m=>m.MerchantId==context.MerchantId && m.EstabelecimentoId==context.EstabelecimentoId)) throw Error("IFOOD_COMMAND_MERCHANT_NOT_ENABLED");
        if(revision!=context.Revision) throw Error("IFOOD_COMMAND_STALE_REVISION");
        if(context.RemoteStatus is "CANCELLED" or "CONCLUDED") throw Error("IFOOD_ORDER_TERMINAL");
        if(!context.DataIsCurrent || context.SynchronizationState != "synced") throw Error("IFOOD_COMMAND_REQUIRES_REVIEW");
        if(context.CancellationState=="requested") throw Error("IFOOD_CANCELLATION_PENDING");
        var order=IfoodOrderNormalizer.Normalize(context.Payload,context.MerchantId,context.ExternalOrderId);
        if(order.RequiresReview || order.Category!="FOOD") throw Error("IFOOD_COMMAND_REQUIRES_REVIEW");
        using(var document=System.Text.Json.JsonDocument.Parse(context.Payload))
        {
            var root=document.RootElement;
            RequireTimezone(root,"createdAt");RequireTimezone(root,"preparationStartDateTime");
            foreach(var name in new[] { "schedule","scheduled" })
            {
                var schedule=IfoodOrderNormalizer.Field(root,name);
                RequireTimezone(schedule,"preparationStartDateTime");RequireTimezone(schedule,"deliveryDateTimeStart");RequireTimezone(schedule,"deliveryDateTimeEnd");
            }
        }
        var notBefore=now; DateTimeOffset? deadline=null;
        var scheduled=order.OrderTiming=="SCHEDULED";
        if(scheduled && (order.PreparationStartUtc==null || order.ScheduledStartUtc==null || order.ScheduledEndUtc==null ||
            order.PreparationStartUtc>order.ScheduledStartUtc)) throw Error("IFOOD_COMMAND_SCHEDULE_INCOMPLETE");
        switch(command)
        {
            case "confirm":
                if(context.RemoteStatus!="PLACED") throw Error("IFOOD_COMMAND_INVALID_STATE");
                // Até IF-01 confirmar a regra do perfil, aplica o prazo mais restritivo dos guias públicos.
                deadline=scheduled && options.ScheduledConfirmationUsesPreparationStart ? order.PreparationStartUtc!.Value.AddMinutes(8) : order.CreatedAtUtc.AddMinutes(8);
                if(scheduled && !options.ScheduledConfirmationUsesPreparationStart)
                    deadline=deadline<order.PreparationStartUtc!.Value.AddMinutes(8) ? deadline : order.PreparationStartUtc.Value.AddMinutes(8);
                if(now>=deadline) throw Error("IFOOD_COMMAND_CONFIRMATION_DEADLINE");
                break;
            case "start_preparation":
                if(context.RemoteStatus!="CONFIRMED" || context.PreparationState!="accepted") throw Error("IFOOD_COMMAND_INVALID_STATE");
                if(order.PreparationStartUtc is {} prep) notBefore=prep>now?prep:now;
                break;
            case "ready_to_pickup":
                if(context.RemoteStatus!="CONFIRMED" || context.PreparationState is not ("accepted" or "preparing")) throw Error("IFOOD_COMMAND_INVALID_STATE");
                if(order.PreparationStartUtc is {} ready) notBefore=ready>now?ready:now;
                break;
            case "dispatch":
                if (context.DeliveryRoutingRequired && !context.DeliveryRouteAccepted) throw Error("IFOOD_DELIVERY_ROUTE_NOT_ACCEPTED");
                if(order.OrderType!="DELIVERY" || order.DeliveredBy!="MERCHANT") throw Error("IFOOD_COMMAND_DISPATCH_NOT_APPLICABLE");
                if(context.RemoteStatus!="CONFIRMED" || context.PreparationState!="ready") throw Error("IFOOD_COMMAND_READY_EVENT_REQUIRED");
                if(scheduled) notBefore=order.ScheduledStartUtc!.Value>now?order.ScheduledStartUtc.Value:now;
                break;
        }
        return new(notBefore,deadline);
    }
    private static void RequireTimezone(System.Text.Json.JsonElement parent,string name)
    {
        var field=IfoodOrderNormalizer.Field(parent,name);
        if(field.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null) return;
        if(field.ValueKind!=System.Text.Json.JsonValueKind.String ||
            !System.Text.RegularExpressions.Regex.IsMatch(field.GetString()??"",@"(Z|[+-]\d{2}:\d{2})$"))
            throw Error("IFOOD_COMMAND_TIMESTAMP_AMBIGUOUS");
    }
    public static DeliveryDomainException Error(string code) => new(409,code,"O pedido mudou ou esta ação não está disponível. Atualize o pedido e confira a operação.");
}
