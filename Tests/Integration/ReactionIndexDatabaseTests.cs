using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class CommunicationDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task ReactionIndexMatchesLookupIsIdempotentAndRollsBack()
    {
        await using var db=await Fixture.Create();
        await db.Execute("CREATE TABLE wa_evento(tipo text,chave text,payload jsonb,recebido_em timestamptz DEFAULT NOW())");
        await db.Migration("20261012_02_wa_reaction_index.sql");
        await db.Migration("20261012_02_wa_reaction_index.sql");
        await db.Execute("""
INSERT INTO wa_evento(tipo,chave,payload)
SELECT 'mensagem',i::text,jsonb_build_object('message',jsonb_build_object('type','reaction','reaction',jsonb_build_object('message_id','wamid.'||i))) FROM generate_series(1,5000) i;
ANALYZE wa_evento;
""");
        await using var connection=await db.Source.OpenConnectionAsync();
        var plan=string.Join("\n",await Dapper.SqlMapper.QueryAsync<string>(connection,"EXPLAIN SELECT payload FROM wa_evento WHERE tipo='mensagem' AND payload->'message'->>'type'='reaction' AND payload->'message'->'reaction'->>'message_id'='wamid.23'"));
        Assert.Contains("ix_wa_evento_reaction_message_id",plan);
        await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","rollback","20261012_02_wa_reaction_index.sql")));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM pg_indexes WHERE schemaname=current_schema() AND indexname='ix_wa_evento_reaction_message_id'"));
    }
}
