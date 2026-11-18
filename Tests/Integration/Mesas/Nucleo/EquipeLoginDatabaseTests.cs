using APIBack.DTOs.Auth;
using APIBack.Options;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

// Mesas P01: login neutro do app da equipe. Mesma montagem de MotoboyConnectionDatabaseTests (o ramo do motoboy não muda).
public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task EquipeLoginIsNeutralAndDoesNotOpenMotoboySession()
    {
        await using var db = await Database.Create();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await db.Execute("""
CREATE TABLE usuario(id int PRIMARY KEY,nome text,email text,senha text,is_super_admin bool,
 ultimo_estabelecimento_acessado uuid,provider text,provider_id text,deleted_at timestamptz,updated_at timestamptz);
CREATE TABLE empresas(id uuid PRIMARY KEY,nome_fantasia text,ativo bool DEFAULT TRUE,pausada bool DEFAULT FALSE);
CREATE TABLE tipo_estabelecimento(id int PRIMARY KEY,nome text);
CREATE TABLE usuario_empresas(id uuid,id_usuario int,id_empresa uuid,tipo_acesso text,status text,ativo bool,created_at timestamptz);
CREATE TABLE usuario_refresh_tokens(id bigserial PRIMARY KEY,id_usuario int,token_hash text UNIQUE,motoboy_login_session_id uuid,
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
INSERT INTO usuario(id,nome,email,is_super_admin) VALUES(7,'Equipe QA','qa@exemplo.invalid',FALSE);
INSERT INTO empresas VALUES(@StoreId,'Empresa QA',TRUE,FALSE);
INSERT INTO tipo_estabelecimento VALUES(1,'restaurante');
UPDATE estabelecimentos SET id_empresa=@StoreId,nome_fantasia='Restaurante QA',id_tipo_estabelecimento=1,modulos_ativos=ARRAY['PEDIDOS','MESAS'];
UPDATE motoboy SET canonical_motoboy_id=id;
""", new { db.StoreId });
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261010_03_motoboy_login_sessions.sql")));
        await db.Execute("UPDATE usuario SET senha=@Senha", new { Senha = APIBack.Security.PasswordSecurity.Hash("qa-password-2026") });
        await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW(),revoked_at=NOW()");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = db.Source.ConnectionString,
            ["Jwt:SecretKey"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            ["Jwt:Issuer"] = "QA", ["Jwt:Audience"] = "QA", ["Jwt:ExpirationMinutes"] = "60",
        }).Build();
        var jwt = new JwtService(configuration);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var auth = new AuthService(configuration, jwt, new Mock<IHttpClientFactory>().Object, cache,
            Microsoft.Extensions.Options.Options.Create(new GoogleOAuthOptions()),
            Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions { Enabled = true }),
            NullLogger<AuthService>.Instance, db.Repository, null);
        LoginRequest Login(string app) => new() { Email = "qa@exemplo.invalid", Senha = "qa-password-2026", ClientApp = app,
            ClientInstanceId = Guid.NewGuid().ToString("D") };

        // Equipe: entra sem loja, sem sessão de motoboy e sem sessão única.
        var equipe = await auth.LoginAsync(Login("equipe"));
        Assert.Null(equipe.MotoboyLoginSessionId);
        Assert.Null(jwt.ValidateToken(equipe.AccessToken).EstabelecimentoId);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_login_sessions"));
        Assert.Null(await db.Scalar<Guid?>("SELECT motoboy_login_session_id FROM usuario_refresh_tokens ORDER BY id DESC LIMIT 1"));

        // Dois logins de equipe no mesmo usuário não se derrubam (sem sessão única).
        var segundo = await auth.LoginAsync(Login("equipe"));
        Assert.Null(segundo.MotoboyLoginSessionId);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_login_sessions"));

        // Motoboy continua abrindo a sessão dele, como antes.
        var motoboy = await auth.LoginAsync(Login("motoboy"));
        Assert.NotNull(motoboy.MotoboyLoginSessionId);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_login_sessions"));
    }
}
