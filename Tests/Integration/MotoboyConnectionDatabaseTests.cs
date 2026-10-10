using System.Security.Cryptography;
using System.Text;
using APIBack.Automation.Repository;
using APIBack.Automation.Repository.Interface;
using APIBack.Automation.Services;
using APIBack.Automation.Validators;
using APIBack.DTOs.Auth;
using APIBack.Middleware;
using APIBack.Extensions;
using APIBack.Options;
using APIBack.Service;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public Task RestaurantSelectionPersistsRefreshAndAuthenticatesBeforeAndAfterRenewal() => CheckRestaurantConnection(false);

    [DeliveryDatabaseFact]
    public Task LegacyMotoboyLinkConnectsRenewsTracksAndRejectsWrongOwnerOrRevokedLink() => CheckRestaurantConnection(true);

    [DeliveryDatabaseFact]
    public Task WebSessionSurvivesMapsPauseWithoutRefreshingLocationAndStillChecksOwner() => CheckRestaurantConnection(true, true);

    private static async Task CheckRestaurantConnection(bool legacyLink, bool web = false)
    {
        await using var db = await Database.Create();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await db.Execute("""
CREATE TABLE usuario(id int PRIMARY KEY,nome text,email text,senha text,is_super_admin bool,
 ultimo_estabelecimento_acessado uuid,provider text,provider_id text,deleted_at timestamptz,updated_at timestamptz);
CREATE TABLE empresas(id uuid PRIMARY KEY,nome_fantasia text,ativo bool DEFAULT TRUE,pausada bool DEFAULT FALSE);
CREATE TABLE tipo_estabelecimento(id int PRIMARY KEY,nome text);
CREATE TABLE usuario_empresas(id uuid,id_usuario int,id_empresa uuid,tipo_acesso text,status text,ativo bool,created_at timestamptz);
CREATE TABLE usuario_refresh_tokens(id bigserial PRIMARY KEY,id_usuario int,token_hash text UNIQUE,
 expires_at timestamptz,created_at timestamptz,created_by_ip text,user_agent text,revoked_at timestamptz,
 revoked_by_ip text,replaced_by_token_hash text,reason_revoked text);
CREATE SEQUENCE motoboy_session_epoch_seq;
CREATE TABLE motoboy_operational_session_events(event_id uuid,session_id uuid,motoboy_id int,
 estabelecimento_id uuid,event_type text,actor_user_id int,reason text,details jsonb,occurred_at_utc timestamptz);
ALTER TABLE motoboy_active_sessions ADD COLUMN contract_version int,
 ADD COLUMN created_at timestamptz;
ALTER TABLE estabelecimentos ADD COLUMN id_empresa uuid,ADD COLUMN nome_fantasia text,
 ADD COLUMN id_tipo_estabelecimento int,ADD COLUMN modulos_ativos text[];
ALTER TABLE usuario_estabelecimentos ADD COLUMN id uuid DEFAULT gen_random_uuid(),
 ADD COLUMN permissoes_customizadas jsonb,ADD COLUMN created_at timestamptz DEFAULT NOW();
INSERT INTO usuario(id,nome,email,is_super_admin) VALUES(7,'Motoboy QA','qa@exemplo.invalid',FALSE);
INSERT INTO empresas VALUES(@StoreId,'Empresa QA',TRUE,FALSE);
INSERT INTO tipo_estabelecimento VALUES(1,'restaurante');
UPDATE estabelecimentos SET id_empresa=@StoreId,nome_fantasia='Restaurante QA',id_tipo_estabelecimento=1,modulos_ativos=ARRAY['DELIVERY'];
UPDATE motoboy SET canonical_motoboy_id=id;
""", new { db.StoreId });
        if (legacyLink) await db.Execute("DELETE FROM usuario_estabelecimentos");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = db.Source.ConnectionString,
            ["Jwt:SecretKey"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            ["Jwt:Issuer"] = "QA", ["Jwt:Audience"] = "QA", ["Jwt:ExpirationMinutes"] = "60",
        }).Build();
        var jwt = new JwtService(configuration);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var auth = new AuthService(configuration,jwt,new Mock<IHttpClientFactory>().Object,cache,
            Microsoft.Extensions.Options.Options.Create(new GoogleOAuthOptions()),
            Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions()),
            NullLogger<AuthService>.Instance,db.Repository);
        var links = new EstabelecimentoSelectionRepository(configuration);
        var selection = new EstabelecimentoSelectionService(links,new EstabelecimentoSelectionValidator(),jwt,auth,
            NullLogger<EstabelecimentoSelectionService>.Instance);
        var selected = await selection.DefinirEstabelecimentoAtivoAsync(7,db.StoreId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selected.RefreshToken)));
        Assert.Equal(hash,await db.Scalar<string>("SELECT token_hash FROM usuario_refresh_tokens"));
        Assert.NotEqual(selected.RefreshToken,hash);
        using var services = new ServiceCollection().AddSingleton(db.Source)
            .AddSingleton<IEstabelecimentoSelectionRepository>(links).BuildServiceProvider();
        async Task<int?> AuthenticatedUser(string token)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers.Authorization = "Bearer " + token;
            await new JwtAuthenticationMiddleware(_ => Task.CompletedTask).InvokeAsync(context,jwt,configuration);
            return context.GetUserId();
        }
        Assert.Equal(7,await AuthenticatedUser(selected.AccessToken));
        if (legacyLink)
        {
            var oldPayload = jwt.ValidateToken(selected.AccessToken);
            oldPayload.VinculoId = Guid.Empty;
            Assert.Equal(7,await AuthenticatedUser(jwt.GenerateToken(oldPayload)));
        }
        var renewed = await auth.RefreshTokenAsync(new RefreshTokenRequest { RefreshToken=selected.RefreshToken },null,null);
        Assert.Equal(db.StoreId,jwt.ValidateToken(renewed.AccessToken).EstabelecimentoId);
        Assert.Equal(7,await AuthenticatedUser(renewed.AccessToken));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM usuario_refresh_tokens WHERE reason_revoked='rotated'"));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM usuario_refresh_tokens WHERE revoked_at IS NULL"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.RefreshTokenAsync(new RefreshTokenRequest { RefreshToken="inexistente-qa" },null,null));

        // O inicio real, a repeticao e a telemetria usam o mesmo vinculo aprovado.
        await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW(),revoked_at=NOW()");
        var attempt = Guid.NewGuid();
        var started = await db.Repository.StartMobileSessionAsync(7,db.StoreId,attempt,"aparelho-qa",clientPlatform: web ? "web" : "native");
        Assert.Equal(started.SessionId,(await db.Repository.StartMobileSessionAsync(7,db.StoreId,attempt,"aparelho-qa")).SessionId);
        var heartbeat = await db.Repository.HeartbeatAsync(started.SessionId,1,started.SessionEpoch);
        Assert.Equal(7,heartbeat.UsuarioId);
        await db.Repository.WriteLocationAsync(started.SessionId,1,started.SessionEpoch,Database.Point(1));
        if (web)
        {
            Assert.Equal("web", started.DeviceType);
            Assert.True((heartbeat.ExpiresAtUtc - heartbeat.LastHeartbeatAtUtc).TotalHours >= 11.9);
            // Simula três minutos no navegador externo, sem GPS ou heartbeat.
            await db.Execute("UPDATE motoboy_active_sessions SET last_heartbeat_at_utc=NOW()-INTERVAL '3 minutes' WHERE session_id=@SessionId", new { started.SessionId });
            var captured = await db.Scalar<DateTimeOffset>("SELECT captured_at_utc FROM motoboy_location_current");
            var received = await db.Scalar<DateTimeOffset>("SELECT received_at_utc FROM motoboy_location_current");
            await db.Repository.ExpireDueSessionsAsync(100);
            Assert.Null((await db.Repository.GetSessionAsync(started.SessionId))!.EndedAtUtc);
            await db.Repository.HeartbeatAsync(started.SessionId,1,started.SessionEpoch);
            Assert.Equal(captured,await db.Scalar<DateTimeOffset>("SELECT captured_at_utc FROM motoboy_location_current"));
            Assert.Equal(received,await db.Scalar<DateTimeOffset>("SELECT received_at_utc FROM motoboy_location_current"));
        }
        var operational = jwt.GenerateToken(new APIBack.Model.Auth.JwtPayload
        {
            UserId=7,EstabelecimentoId=db.StoreId,MotoboyId=1,MotoboySessionId=started.SessionId,SessionEpoch=started.SessionEpoch,
            TipoAcesso="motoboy",TokenUse="delivery_operational",ClientType="mobile",
        });
        Assert.Equal(7,await AuthenticatedUser(operational));
        await db.Execute("UPDATE motoboy SET id_usuario=8");
        Assert.Null(await AuthenticatedUser(operational));
        if (legacyLink) Assert.Null(await AuthenticatedUser(renewed.AccessToken));
        await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.HeartbeatAsync(started.SessionId,1,started.SessionEpoch));
        await db.Execute("UPDATE motoboy SET id_usuario=7;UPDATE motoboy_estabelecimento SET ativo=FALSE");
        Assert.Null(await AuthenticatedUser(operational));
        if (legacyLink) Assert.Null(await AuthenticatedUser(renewed.AccessToken));
        await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.WriteLocationAsync(started.SessionId,1,started.SessionEpoch,Database.Point(2)));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_samples"));
    }
}
