using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Helpers;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Simulador;
using APIBack.Model.Delivery;
using APIBack.Model.Enum;
using APIBack.Repository;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Service
{
    public interface ISimuladorPedidoService
    {
        Task<IReadOnlyList<SimPedidoResumoDto>> ListAsync(Guid est, string? busca, string? status, int limit);
        Task<SimPedidoDetalheDto> GetAsync(Guid est, int pedidoId);
        Task<SimPedidoDetalheDto> CreateAsync(Guid est, int userId, SimPedidoCriarRequest request);
        Task<SimPedidoDetalheDto> AlterarAsync(Guid est, int userId, int pedidoId, SimPedidoCriarRequest request);
        Task<SimPedidoDetalheDto> EtapaAsync(Guid est, int userId, int pedidoId, SimPedidoEtapaRequest request);
        Task<SimPedidoDetalheDto> StatusAsync(Guid est, int userId, int pedidoId, SimPedidoStatusRequest request);
        Task<SimPedidoDetalheDto> CloneAsync(Guid est, int userId, int pedidoId);
        Task<SimPedidoDetalheDto> InjectAsync(Guid est, int userId, int pedidoId);
        Task<SimPedidoDetalheDto> EventoAsync(Guid est, int userId, int pedidoId, SimPedidoEventoRequest request);
        Task<SimPedidoDetalheDto> MotoboyAsync(Guid est, int userId, int pedidoId, int motoboyId);
        Task<SimPedidoDetalheDto> AnexarClienteAsync(Guid est, int userId, int pedidoId, Guid clienteId);
        Task RemoverAsync(Guid est, int userId, int pedidoId);
    }

    /// <summary>
    /// Simulador de pedido. Cria e conduz pedidos de TESTE (origem 'simulador') pelos MESMOS comandos do fluxo real
    /// (nucleo de pedido, fila, atribuir, coletar, entregar): o que aqui e proibido no real tambem e proibido aqui
    /// (ver <see cref="SimuladorPedidoRules"/>). Cada passo grava um evento no log do simulador.
    /// </summary>
    public sealed class SimuladorPedidoService : ISimuladorPedidoService
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly IPedidoCoreService _core;
        private readonly IPedidoQueueService _queue;
        private readonly IPedidoConsultaRepository _consulta;
        private readonly ISimuladorRepository _eventos;

        public SimuladorPedidoService(
            NpgsqlDataSource dataSource,
            IPedidoCoreService core,
            IPedidoQueueService queue,
            IPedidoConsultaRepository consulta,
            ISimuladorRepository eventos)
        {
            _dataSource = dataSource;
            _core = core;
            _queue = queue;
            _consulta = consulta;
            _eventos = eventos;
        }

        // ------------------------------------------------------------------ leitura

        private sealed class PedidoRow
        {
            public int Id { get; set; }
            public int Status { get; set; }
            public string? Origem { get; set; }
            public string? Canal { get; set; }
            public int? MotoboyId { get; set; }
            public DateTime? ConfirmadoEm { get; set; }
            public DateTime? PreparoEm { get; set; }
            public string? CodigoEntrega { get; set; }
            public string? TelefoneCliente { get; set; }
            public string? NomeCliente { get; set; }
            public Guid? ConversaId { get; set; }
        }

        private async Task<PedidoRow> LoadRowAsync(NpgsqlConnection connection, Guid est, int pedidoId)
        {
            return await connection.QuerySingleOrDefaultAsync<PedidoRow>(@"
SELECT p.id AS Id, COALESCE(p.status_pedido, 1) AS Status, p.origem AS Origem, p.canal AS Canal,
       COALESCE(p.motoboy_responsavel, (SELECT rs.motoboy_id FROM delivery_route_stops rs
                                         WHERE rs.pedido_id = p.id AND rs.stop_status IN ('assigned', 'en_route') LIMIT 1)) AS MotoboyId,
       p.confirmado_em_utc AS ConfirmadoEm, p.preparo_em_utc AS PreparoEm, p.codigo_entrega::text AS CodigoEntrega,
       p.telefone_cliente::text AS TelefoneCliente, p.nome_cliente::text AS NomeCliente, p.conversa_id AS ConversaId
  FROM pedido p
 WHERE p.id = @Id AND p.id_estabelecimento = @Est;", new { Id = pedidoId, Est = est })
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
        }

        private async Task<PedidoRow> LoadRowAsync(Guid est, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await LoadRowAsync(connection, est, pedidoId);
        }

        private static bool IsSimulated(PedidoRow row) =>
            string.Equals(row.Origem, PedidoOrigem.Simulador, StringComparison.OrdinalIgnoreCase);

        private static StatusPedido StatusOf(PedidoRow row) => StatusPedidoExtensions.FromDbValue(row.Status) ?? StatusPedido.Pendente;

        public async Task<IReadOnlyList<SimPedidoResumoDto>> ListAsync(Guid est, string? busca, string? status, int limit)
        {
            var term = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim();
            var statusNumbers = (status ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToLowerInvariant() switch
                {
                    "pendente" => 1, "em_rota" => 2, "concluido" => 3, "cancelado" => 4, "atribuido" => 5, _ => 0
                })
                .Where(number => number > 0)
                .ToArray();

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(int Id, int Status, string? Origem, decimal? Total, string? Data, string? Nome, int? MotoboyId, string? MotoboyNome)>(@"
SELECT p.id, COALESCE(p.status_pedido, 1), p.origem, p.value, p.data_pedido::text, p.nome_cliente::text,
       p.motoboy_responsavel, m.nome::text
  FROM pedido p
  LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
 WHERE p.id_estabelecimento = @Est AND p.origem = 'simulador' AND COALESCE(p.status_pedido, 1) <> 6
   AND (@Status::int[] IS NULL OR COALESCE(p.status_pedido, 1) = ANY(@Status::int[]))
   AND (@Term IS NULL OR p.id::text = @Term OR p.nome_cliente::text ILIKE '%' || @Term || '%')
 ORDER BY p.id DESC
 LIMIT @Limit;", new
            {
                Est = est, Status = statusNumbers.Length == 0 ? null : statusNumbers, Term = term, Limit = Math.Clamp(limit, 1, 100)
            });

            return rows.Select(row => new SimPedidoResumoDto
            {
                Id = row.Id,
                IdExibicao = SimuladorPedidoRules.DisplayId(row.Id, true),
                Status = (StatusPedidoExtensions.FromDbValue(row.Status) ?? StatusPedido.Pendente).ToApiKey(),
                Origem = row.Origem ?? PedidoOrigem.Simulador,
                Total = row.Total,
                CriadoEm = DeliveryRules.ParseStoredDateTime(row.Data),
                NomeCliente = row.Nome,
                MotoboyId = row.MotoboyId,
                MotoboyNome = row.MotoboyNome
            }).ToList();
        }

        public async Task<SimPedidoDetalheDto> GetAsync(Guid est, int pedidoId)
        {
            var baseDetail = await _consulta.GetAsync(est, pedidoId)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            await using var connection = await _dataSource.OpenConnectionAsync();
            var row = await LoadRowAsync(connection, est, pedidoId);
            var status = StatusOf(row);

            var extra = await connection.QuerySingleAsync<(DateTime? Saiu, DateTime? Entregue, string? Loja, double? LojaLat, double? LojaLng)>(@"
SELECT (SELECT MAX(rs.started_at_utc) FROM delivery_route_stops rs
         WHERE rs.pedido_id = @Id AND rs.stop_status IN ('en_route', 'completed')),
       (SELECT MAX(rs.completed_at_utc) FROM delivery_route_stops rs WHERE rs.pedido_id = @Id AND rs.stop_status = 'completed'),
       e.nome_fantasia::text, e.latitude::double precision, e.longitude::double precision
  FROM estabelecimentos e WHERE e.id = @Est;", new { Id = pedidoId, Est = est });

            SimMotoboyCardDto? motoboy = null;
            if (row.MotoboyId.HasValue)
            {
                motoboy = await connection.QuerySingleOrDefaultAsync<SimMotoboyCardDto>(@"
SELECT m.id AS Id, COALESCE(m.nome, '') AS Nome, m.telefone::text AS Telefone, m.avatar AS Avatar,
       (SELECT COUNT(*)::int FROM delivery_route_stops rs
         WHERE rs.motoboy_id = m.id AND rs.estabelecimento_id = @Est AND rs.stop_status = 'completed') AS Entregas,
       EXISTS (SELECT 1 FROM motoboy_active_sessions s
                WHERE s.motoboy_id = m.id AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW()) AS Online,
       lc.latitude AS Latitude, lc.longitude AS Longitude
  FROM motoboy m
  LEFT JOIN LATERAL (
      SELECT c.latitude, c.longitude FROM motoboy_location_current c
       WHERE c.motoboy_id = m.id ORDER BY c.received_at_utc DESC LIMIT 1) lc ON TRUE
 WHERE m.id = @MotoboyId;", new { MotoboyId = row.MotoboyId.Value, Est = est });
            }

            // O cliente cadastrado que tem este telefone (o mesmo do chat).
            Guid? clienteId = null;
            var national = ClienteRules.NationalDigits(row.TelefoneCliente);
            if (!string.IsNullOrEmpty(national) && national.Length is 10 or 11)
            {
                clienteId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
SELECT id FROM clientes WHERE id_estabelecimento = @Est AND telefone_e164 = @Tel AND ativo = TRUE LIMIT 1;",
                    new { Est = est, Tel = TelefoneHelper.ToE164(national) });
            }

            double? distance = null;
            int? eta = null;
            if (baseDetail.Latitude.HasValue && baseDetail.Longitude.HasValue)
            {
                double? fromLat = motoboy?.Latitude ?? extra.LojaLat;
                double? fromLng = motoboy?.Longitude ?? extra.LojaLng;
                if (extra.LojaLat.HasValue && extra.LojaLng.HasValue)
                {
                    distance = Math.Round(OrderCoreRules.DistanceKm(extra.LojaLat.Value, extra.LojaLng.Value, baseDetail.Latitude.Value, baseDetail.Longitude.Value), 1);
                }
                if (fromLat.HasValue && fromLng.HasValue && status != StatusPedido.Concluido && status != StatusPedido.Cancelado)
                {
                    var km = OrderCoreRules.DistanceKm(fromLat.Value, fromLng.Value, baseDetail.Latitude.Value, baseDetail.Longitude.Value);
                    eta = Math.Max(1, (int)Math.Round(km / 30d * 60d)); // 30 km/h: velocidade media de moto na cidade
                }
            }

            var confirmado = row.ConfirmadoEm.HasValue;
            var preparo = row.PreparoEm.HasValue;
            var simulado = IsSimulated(row);
            return new SimPedidoDetalheDto
            {
                Pedido = baseDetail,
                IdExibicao = SimuladorPedidoRules.DisplayId(pedidoId, simulado),
                Simulado = simulado,
                Canal = row.Canal,
                ClienteId = clienteId,
                Etapa = SimuladorPedidoRules.EtapaAtual(status, confirmado, preparo),
                ConfirmadoEm = row.ConfirmadoEm,
                PreparoEm = row.PreparoEm,
                SaiuEm = extra.Saiu,
                EntregueEm = extra.Entregue,
                Motoboy = motoboy,
                RestauranteNome = extra.Loja,
                RestauranteLatitude = extra.LojaLat,
                RestauranteLongitude = extra.LojaLng,
                DistanciaKm = distance,
                EtaMinutos = eta,
                RotaStatus = status switch
                {
                    StatusPedido.Cancelado => "cancelada",
                    StatusPedido.Concluido => "concluida",
                    StatusPedido.EmRota => "em_rota",
                    StatusPedido.Atribuido => "aguardando",
                    _ => "sem_motoboy"
                },
                Proximos = simulado ? SimuladorPedidoRules.Proximos(status, confirmado, preparo) : new List<string>()
            };
        }

        // ------------------------------------------------------------------ criar / clonar

        /// <summary>Valida o pedido do simulador e monta o pedido do nucleo (cliente, endereco, itens, automacoes).</summary>
        private async Task<(CreatePedidoRequest Create, string Canal, SimAlvo Alvo)> PrepareAsync(Guid est, SimPedidoCriarRequest request, bool allowFill)
        {
            if (request == null) throw new DeliveryDomainException(400, "INVALID_REQUEST", "Corpo da requisicao obrigatorio.");

            var canal = SimuladorPedidoRules.NormalizeChannel(request.Canal)
                ?? throw new DeliveryDomainException(422, "INVALID_CHANNEL", $"Canal invalido. Use: {string.Join(", ", SimuladorPedidoRules.Channels)}.");
            var alvo = request.StatusAlvo == null ? SimAlvo.Recebido
                : SimuladorPedidoRules.ParseAlvo(request.StatusAlvo)
                  ?? throw new DeliveryDomainException(422, "INVALID_TARGET", "Status alvo invalido.");
            var fill = allowFill && request.StatusAlvo != null; // "criar do nada": completa o que faltar com dados de teste

            await using var connection = await _dataSource.OpenConnectionAsync();

            var loja = await connection.QuerySingleAsync<(string? Cidade, string? Uf, double? Lat, double? Lng)>(
                "SELECT cidade::text, uf::text, latitude::double precision, longitude::double precision FROM estabelecimentos WHERE id = @Est;", new { Est = est });

            string? nome = request.NomeCliente, telefone = request.TelefoneCliente;
            string? rua = request.Rua, numero = request.Numero, bairro = request.Bairro, cidade = request.Cidade, estado = request.Estado, cep = request.Cep;
            double? lat = request.Latitude, lng = request.Longitude;
            Guid? conversaId = null;
            var optIn = false;

            if (request.ClienteId.HasValue)
            {
                var cliente = await connection.QuerySingleOrDefaultAsync<(string? Nome, string? Tel, string? Rua, string? Num, string? Bairro, string? Cidade, string? Uf, string? Cep, double? Lat, double? Lng, bool Simulado)>(@"
SELECT nome, telefone_e164, logradouro, numero, bairro, cidade, uf, cep, latitude, longitude, simulado
  FROM clientes WHERE id = @Id AND id_estabelecimento = @Est AND ativo = TRUE;", new { Id = request.ClienteId.Value, Est = est });
                if (cliente.Tel == null && cliente.Nome == null)
                {
                    throw new DeliveryDomainException(404, "CLIENT_NOT_FOUND", "Cliente nao encontrado.");
                }
                nome = string.IsNullOrWhiteSpace(nome) ? cliente.Nome : nome;
                telefone = string.IsNullOrWhiteSpace(telefone) ? cliente.Tel : telefone;
                rua = string.IsNullOrWhiteSpace(rua) ? cliente.Rua : rua;
                numero = string.IsNullOrWhiteSpace(numero) ? cliente.Num : numero;
                bairro = string.IsNullOrWhiteSpace(bairro) ? cliente.Bairro : bairro;
                cidade = string.IsNullOrWhiteSpace(cidade) ? cliente.Cidade : cidade;
                estado = string.IsNullOrWhiteSpace(estado) ? cliente.Uf : estado;
                cep = string.IsNullOrWhiteSpace(cep) ? cliente.Cep : cep;
                lat ??= cliente.Lat;
                lng ??= cliente.Lng;

                // Automacoes reais: liga o pedido a conversa e aceita os avisos de rastreio. So para cliente de TESTE:
                // com cliente real o aviso poderia ir para o WhatsApp de uma pessoa de verdade.
                if (cliente.Simulado && request.AutomacoesReais != false)
                {
                    conversaId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
SELECT id FROM conversas WHERE id_cliente = @Id AND id_estabelecimento = @Est ORDER BY data_criacao DESC LIMIT 1;",
                        new { Id = request.ClienteId.Value, Est = est });
                    optIn = true;
                }
            }

            if (fill)
            {
                nome = string.IsNullOrWhiteSpace(nome) ? "Cliente Teste" : nome;
                rua = string.IsNullOrWhiteSpace(rua) ? "Rua Simulada" : rua;
                numero = string.IsNullOrWhiteSpace(numero) ? "100" : numero;
                bairro = string.IsNullOrWhiteSpace(bairro) ? "Centro" : bairro;
                cidade = string.IsNullOrWhiteSpace(cidade) ? loja.Cidade ?? "Uberlandia" : cidade;
                estado = string.IsNullOrWhiteSpace(estado) ? loja.Uf ?? "MG" : estado;
                if ((!lat.HasValue || !lng.HasValue) && loja.Lat.HasValue && loja.Lng.HasValue)
                {
                    // Ponto de teste a ate ~2 km da loja (nunca a mesma coordenada, para o mapa nao empilhar pinos).
                    var random = new Random();
                    lat = loja.Lat.Value + (random.NextDouble() - 0.5) * 0.03;
                    lng = loja.Lng.Value + (random.NextDouble() - 0.5) * 0.03;
                }
            }

            var itens = request.Itens is { Count: > 0 } ? request.Itens
                : fill ? new List<PedidoItemRequest> { new() { Nome = "Item de teste", Quantidade = 1, PrecoUnitario = 25m } }
                : request.Itens;

            var create = new CreatePedidoRequest
            {
                Origem = PedidoOrigem.Simulador,
                NomeCliente = nome,
                TelefoneCliente = telefone,
                Rua = rua, Numero = numero, Complemento = request.Complemento, Bairro = bairro, Cidade = cidade, Estado = estado, Cep = cep,
                Latitude = lat, Longitude = lng,
                Itens = itens,
                Observacoes = request.Observacoes,
                TipoPagamento = request.TipoPagamento,
                Troco = request.Troco,
                TaxaEntrega = request.TaxaEntrega,
                PrevisaoMinutos = request.PrevisaoMinutos,
                ConversaId = conversaId,
                RastreioOptIn = optIn ? true : null
            };


            return (create, canal, alvo);
        }

        public async Task<SimPedidoDetalheDto> CreateAsync(Guid est, int userId, SimPedidoCriarRequest request)
        {
            var (create, canal, alvo) = await PrepareAsync(est, request, allowFill: true);
            await using var connection = await _dataSource.OpenConnectionAsync();

            var created = await _core.CreateAsync(est, userId, create, null);
            await connection.ExecuteAsync("UPDATE pedido SET canal = @Canal WHERE id = @Id AND id_estabelecimento = @Est;",
                new { Canal = canal, Id = created.Id, Est = est });

            await LogAsync(est, userId, "pedido", created.Id, "pedido_criado", "Pedido criado em teste",
                $"{SimuladorPedidoRules.DisplayId(created.Id, true)} - {(created.Total ?? 0m):0.00} - {create.Itens?.Count ?? 0} item(ns)");

            if (alvo != SimAlvo.Recebido)
            {
                return await ForceAsync(est, userId, created.Id, alvo, request.MotoboyId);
            }
            return await GetAsync(est, created.Id);
        }

        public async Task<SimPedidoDetalheDto> CloneAsync(Guid est, int userId, int pedidoId)
        {
            var source = await GetAsync(est, pedidoId);
            var pedido = source.Pedido;
            var request = new SimPedidoCriarRequest
            {
                NomeCliente = pedido.NomeCliente,
                TelefoneCliente = pedido.TelefoneCliente,
                Canal = source.Canal,
                Rua = pedido.Rua, Numero = pedido.Numero, Bairro = pedido.Bairro, Cidade = pedido.Cidade, Estado = pedido.Estado, Cep = pedido.Cep,
                Latitude = pedido.Latitude, Longitude = pedido.Longitude,
                Observacoes = pedido.Observacoes,
                TipoPagamento = pedido.FormaPagamento,
                Troco = pedido.Troco,
                TaxaEntrega = pedido.TaxaEntrega,
                ClienteId = source.ClienteId,
                // Itens viram linhas avulsas: o preco que estava no pedido e o que o clone repete.
                Itens = pedido.Itens.Select(item => new PedidoItemRequest
                {
                    Nome = item.Nome,
                    Quantidade = Math.Max(1, item.Quantidade),
                    PrecoUnitario = item.PrecoUnitario ?? 0m,
                    Observacao = item.Observacao
                }).ToList()
            };
            var created = await CreateAsync(est, userId, request);
            await LogAsync(est, userId, "pedido", created.Pedido.Id, "pedido_clonado", "Pedido clonado",
                $"Copia de {SimuladorPedidoRules.DisplayId(pedidoId, source.Simulado)}");
            return created;
        }

        /// <summary>
        /// Salvar alteracoes do formulario. Vale a regra REAL de edicao do pedido: so rascunho/pendente e sem motoboy
        /// (o nucleo recusa o resto com ORDER_NOT_EDITABLE): para mexer nos itens de um pedido na fila, tire-o da fila.
        /// </summary>
        public async Task<SimPedidoDetalheDto> AlterarAsync(Guid est, int userId, int pedidoId, SimPedidoCriarRequest request)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            var (create, canal, _) = await PrepareAsync(est, request, allowFill: false);
            create.ConversaId ??= row.ConversaId;
            await _core.UpdateAsync(est, userId, pedidoId, create);
            await ExecAsyncCanal(canal, pedidoId, est);
            await LogAsync(est, userId, "pedido", pedidoId, "pedido_alterado", "Pedido alterado", "Dados e itens editados no simulador");
            return await GetAsync(est, pedidoId);
        }

        private async Task ExecAsyncCanal(string canal, int pedidoId, Guid est)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE pedido SET canal = @Canal WHERE id = @Id AND id_estabelecimento = @Est;", new { Canal = canal, Id = pedidoId, Est = est });
        }

        // ------------------------------------------------------------------ etapas

        public async Task<SimPedidoDetalheDto> EtapaAsync(Guid est, int userId, int pedidoId, SimPedidoEtapaRequest request)
        {
            var etapa = SimuladorPedidoRules.ParseEtapa(request?.Etapa)
                ?? throw new DeliveryDomainException(422, "INVALID_STEP", "Etapa invalida. Use: confirmar, preparo, saiu ou entregue.");
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);

            var error = SimuladorPedidoRules.ValidateEtapa(etapa, StatusOf(row), row.ConfirmadoEm.HasValue, row.PreparoEm.HasValue);
            if (error != null) throw new DeliveryDomainException(409, "STEP_NOT_ALLOWED", error);

            await ApplyEtapaAsync(est, userId, row, etapa, request?.MotoboyId);
            return await GetAsync(est, pedidoId);
        }

        private async Task ApplyEtapaAsync(Guid est, int userId, PedidoRow row, SimEtapa etapa, int? motoboyId)
        {
            var id = row.Id;
            switch (etapa)
            {
                case SimEtapa.Confirmar:
                    await ExecAsync("UPDATE pedido SET confirmado_em_utc = NOW() WHERE id = @Id AND id_estabelecimento = @Est;", id, est);
                    await LogAsync(est, userId, "pedido", id, "pedido_confirmado", "Pedido confirmado", "Status alterado manualmente no simulador");
                    row.ConfirmadoEm = DateTime.UtcNow;
                    break;
                case SimEtapa.Preparo:
                    await ExecAsync("UPDATE pedido SET preparo_em_utc = NOW() WHERE id = @Id AND id_estabelecimento = @Est;", id, est);
                    await LogAsync(est, userId, "pedido", id, "pedido_em_preparo", "Pedido em preparo", "Status alterado manualmente no simulador");
                    row.PreparoEm = DateTime.UtcNow;
                    break;
                case SimEtapa.Saiu:
                {
                    // Regra: sair para entrega exige motoboy. Escolhido, o que ja esta no pedido ou, na auto-atribuicao, um online.
                    var motoboy = motoboyId ?? row.MotoboyId ?? await PickMotoboyAsync(est);
                    await _queue.AssignAsync(est, userId, motoboy, id);
                    row.MotoboyId = motoboy;
                    var nome = await MotoboyNomeAsync(motoboy);
                    await LogAsync(est, userId, "pedido", id, "pedido_saiu", "Saiu para entrega", $"Motoboy {nome} atribuido");
                    await LogAsync(est, userId, "motoboy", motoboy.ToString(), "pedido_atribuido", $"Pedido {SimuladorPedidoRules.DisplayId(id, true)} atribuido", "Entrega iniciada");
                    break;
                }
                case SimEtapa.Entregue:
                {
                    var motoboy = row.MotoboyId
                        ?? throw new DeliveryDomainException(409, "NO_MOTOBOY", "O pedido nao tem motoboy: nao ha quem entregue.");
                    await using var connection = await _dataSource.OpenConnectionAsync();
                    var stop = await connection.QuerySingleOrDefaultAsync<(string Status, DateTime? Picked, DateTime? Arrived)?>(@"
SELECT stop_status, picked_up_at_utc, arrived_at_utc FROM delivery_route_stops
 WHERE pedido_id = @Id AND stop_status IN ('assigned', 'en_route') ORDER BY id DESC LIMIT 1;", new { Id = id });
                    if (stop is not { Status: "en_route" })
                    {
                        throw new DeliveryDomainException(409, "NOT_CURRENT_DELIVERY",
                            "Este pedido ainda nao e a entrega atual do motoboy (ha outra entrega na frente na fila).");
                    }
                    // Mesmos passos do app do motoboy: coletou, chegou, entregou (com o codigo, se a loja exige).
                    if (!stop.Value.Picked.HasValue) await _queue.MarkPickedUpAsync(est, motoboy);
                    if (!stop.Value.Arrived.HasValue) await _queue.MarkArrivedAsync(est, motoboy);
                    await _queue.DeliverCurrentAsync(est, motoboy, row.CodigoEntrega);
                    await LogAsync(est, userId, "pedido", id, "pedido_entregue", "Pedido entregue", "Status alterado manualmente no simulador");
                    await LogAsync(est, userId, "motoboy", motoboy.ToString(), "entrega_concluida", "Entrega concluida (simulacao)", $"Pedido {SimuladorPedidoRules.DisplayId(id, true)} entregue com sucesso");
                    break;
                }
            }
        }

        /// <summary>Motoboy simulado online, de preferencia sem entrega atual e com a menor fila.</summary>
        private async Task<int> PickMotoboyAsync(Guid est)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var id = await connection.QuerySingleOrDefaultAsync<int?>(@"
SELECT m.id
  FROM motoboy m
  JOIN motoboy_estabelecimento me ON me.motoboy_id = m.id AND me.estabelecimento_id = @Est AND me.ativo = TRUE AND me.simulator_enabled = TRUE
 WHERE m.is_simulated = TRUE
   AND EXISTS (SELECT 1 FROM motoboy_active_sessions s
                WHERE s.motoboy_id = m.id AND s.origin = 'simulator' AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW())
 ORDER BY EXISTS (SELECT 1 FROM delivery_route_stops rs WHERE rs.motoboy_id = m.id AND rs.stop_status = 'en_route') ASC,
          (SELECT COUNT(*) FROM delivery_route_stops rs WHERE rs.motoboy_id = m.id AND rs.stop_status IN ('assigned', 'en_route')) ASC,
          m.id
 LIMIT 1;", new { Est = est });
            return id ?? throw new DeliveryDomainException(409, "NO_MOTOBOY_AVAILABLE",
                "Nenhum motoboy simulado esta online. Coloque um motoboy online no simulador de motoboy ou escolha um na lista.");
        }

        private async Task<string> MotoboyNomeAsync(int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<string?>("SELECT nome::text FROM motoboy WHERE id = @Id;", new { Id = motoboyId })
                   ?? $"#{motoboyId}";
        }

        // ------------------------------------------------------------------ editar status (forcar, com as regras)

        public async Task<SimPedidoDetalheDto> StatusAsync(Guid est, int userId, int pedidoId, SimPedidoStatusRequest request)
        {
            var alvo = SimuladorPedidoRules.ParseAlvo(request?.Alvo)
                ?? throw new DeliveryDomainException(422, "INVALID_TARGET", "Status invalido. Use: recebido, confirmado, em_preparo, saiu, entregue ou cancelado.");
            return await ForceAsync(est, userId, pedidoId, alvo, request?.MotoboyId);
        }

        private async Task<SimPedidoDetalheDto> ForceAsync(Guid est, int userId, int pedidoId, SimAlvo alvo, int? motoboyId)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            var status = StatusOf(row);

            if (alvo == SimAlvo.Cancelado)
            {
                if (status != StatusPedido.Cancelado)
                {
                    if (status == StatusPedido.Concluido)
                    {
                        throw new DeliveryDomainException(409, "ORDER_FINISHED", "Pedido entregue nao pode ser cancelado. Reabra o pedido primeiro.");
                    }
                    await _queue.CancelAsync(est, userId, pedidoId, "Cancelado no simulador");
                    await LogAsync(est, userId, "pedido", pedidoId, "pedido_cancelado", "Pedido cancelado", "Status alterado manualmente no simulador", "atencao");
                }
                return await GetAsync(est, pedidoId);
            }

            // Regra: para chegar em rota/entregue TEM que haver motoboy. Falha antes de mexer em qualquer coisa.
            int? motoboy = null;
            if (SimuladorPedidoRules.RequiresMotoboy(alvo))
            {
                motoboy = motoboyId ?? row.MotoboyId ?? await PickMotoboyAsync(est);
            }

            // 1) Volta a uma base pendente quando o alvo esta "atras" do estado atual.
            if (status is StatusPedido.Concluido or StatusPedido.Cancelado)
            {
                await _queue.ReopenPedidoForSimulatorAsync(est, userId, pedidoId);
                await LogAsync(est, userId, "pedido", pedidoId, "pedido_reaberto", "Pedido reaberto", "Voltou a pendente, sem motoboy", "atencao");
            }
            else if (status is StatusPedido.Atribuido or StatusPedido.EmRota && !SimuladorPedidoRules.RequiresMotoboy(alvo))
            {
                await _queue.RemoveAsync(est, userId, pedidoId);
                await LogAsync(est, userId, "pedido", pedidoId, "pedido_removido_fila", "Pedido saiu da fila", "Voltou a pendente", "atencao");
            }

            // 2) Etapas de preparo conforme o alvo.
            switch (alvo)
            {
                case SimAlvo.Recebido:
                    await ExecAsync("UPDATE pedido SET confirmado_em_utc = NULL, preparo_em_utc = NULL WHERE id = @Id AND id_estabelecimento = @Est;", pedidoId, est);
                    break;
                case SimAlvo.Confirmado:
                    await ExecAsync("UPDATE pedido SET confirmado_em_utc = COALESCE(confirmado_em_utc, NOW()), preparo_em_utc = NULL WHERE id = @Id AND id_estabelecimento = @Est;", pedidoId, est);
                    break;
                default:
                    await ExecAsync("UPDATE pedido SET confirmado_em_utc = COALESCE(confirmado_em_utc, NOW()), preparo_em_utc = COALESCE(preparo_em_utc, NOW()) WHERE id = @Id AND id_estabelecimento = @Est;", pedidoId, est);
                    break;
            }

            // 3) Fila e entrega, pelos comandos reais.
            if (SimuladorPedidoRules.RequiresMotoboy(alvo))
            {
                row = await LoadRowAsync(est, pedidoId);
                status = StatusOf(row);
                if (status == StatusPedido.Pendente || (status == StatusPedido.Atribuido && motoboyId.HasValue && row.MotoboyId != motoboyId))
                {
                    await _queue.AssignAsync(est, userId, motoboy!.Value, pedidoId);
                    row = await LoadRowAsync(est, pedidoId);
                    await LogAsync(est, userId, "pedido", pedidoId, "pedido_saiu", "Saiu para entrega", $"Motoboy {await MotoboyNomeAsync(motoboy.Value)} atribuido");
                }
                if (alvo == SimAlvo.Entregue && StatusOf(row) != StatusPedido.Concluido)
                {
                    row = await LoadRowAsync(est, pedidoId);
                    var error = SimuladorPedidoRules.ValidateEtapa(SimEtapa.Entregue, StatusOf(row), true, true);
                    if (error != null) throw new DeliveryDomainException(409, "STEP_NOT_ALLOWED", error);
                    await ApplyEtapaAsync(est, userId, row, SimEtapa.Entregue, null);
                }
            }
            else
            {
                await LogAsync(est, userId, "pedido", pedidoId, "pedido_status", $"Status alterado para {alvo}", "Status alterado manualmente no simulador");
            }

            return await GetAsync(est, pedidoId);
        }

        // ------------------------------------------------------------------ acoes rapidas

        public async Task<SimPedidoDetalheDto> InjectAsync(Guid est, int userId, int pedidoId)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            if (StatusOf(row) is StatusPedido.Concluido or StatusPedido.Cancelado)
            {
                throw new DeliveryDomainException(409, "ORDER_FINISHED", "Pedido terminado nao volta ao painel. Reabra-o primeiro.");
            }

            // Reemite o evento de "pedido novo" no tempo real: o painel recarrega e mostra o pedido como chegando agora.
            await _queue.PublishPedidoEventForSimulatorAsync(est, userId, pedidoId, "created");
            await LogAsync(est, userId, "pedido", pedidoId, "pedido_injetado", "Pedido injetado no painel", "Evento de pedido enviado ao painel em tempo real");
            return await GetAsync(est, pedidoId);
        }

        public async Task<SimPedidoDetalheDto> EventoAsync(Guid est, int userId, int pedidoId, SimPedidoEventoRequest request)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            var tipo = (request?.Tipo ?? string.Empty).Trim().ToLowerInvariant();

            switch (tipo)
            {
                case "pagamento_confirmado":
                    await _queue.UpdatePedidoForSimulatorAsync(est, userId, pedidoId, new SimulatorPedidoRequest { StatusPagamento = "Pago" });
                    await LogAsync(est, userId, "pedido", pedidoId, "pagamento_confirmado", "Pagamento confirmado", "Evento manual do simulador");
                    break;
                case "atraso":
                {
                    if (StatusOf(row) is StatusPedido.Concluido or StatusPedido.Cancelado)
                    {
                        throw new DeliveryDomainException(409, "ORDER_FINISHED", "Pedido terminado nao atrasa.");
                    }
                    var minutos = Math.Clamp(request?.Minutos ?? 12, 1, 24 * 60);
                    await _queue.UpdatePedidoForSimulatorAsync(est, userId, pedidoId, new SimulatorPedidoRequest { PrevisaoEmMinutos = -minutos });
                    await LogAsync(est, userId, "pedido", pedidoId, "atraso_detectado", "Atraso detectado", $"Pedido com +{minutos} min de atraso", "atencao");
                    break;
                }
                case "cliente_ligou":
                    await LogAsync(est, userId, "pedido", pedidoId, "cliente_ligou", "Cliente ligou", request?.Texto ?? "Cliente perguntou pelo pedido", "atencao");
                    break;
                case "falha_gps":
                    await LogAsync(est, userId, "pedido", pedidoId, "falha_gps", "Falha de GPS", request?.Texto ?? "Motoboy perdeu o sinal de localizacao", "atencao");
                    break;
                case "nota":
                    await LogAsync(est, userId, "pedido", pedidoId, "nota", "Nota do simulador", request?.Texto);
                    break;
                default:
                    throw new DeliveryDomainException(422, "INVALID_EVENT", "Evento invalido. Use: pagamento_confirmado, atraso, cliente_ligou, falha_gps ou nota.");
            }
            return await GetAsync(est, pedidoId);
        }

        public async Task<SimPedidoDetalheDto> MotoboyAsync(Guid est, int userId, int pedidoId, int motoboyId)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            var status = StatusOf(row);

            switch (status)
            {
                case StatusPedido.Pendente:
                case StatusPedido.Atribuido:
                    await _queue.AssignAsync(est, userId, motoboyId, pedidoId);
                    break;
                case StatusPedido.EmRota:
                    // Pedido em rota nao e "reatribuido": muda de motoboy por transferencia, que trata a entrega atual.
                    await _queue.TransferByOperatorAsync(est, userId, pedidoId, motoboyId, "Alterado no simulador");
                    break;
                default:
                    throw new DeliveryDomainException(409, "ORDER_FINISHED", "Pedido terminado nao muda de motoboy.");
            }
            await LogAsync(est, userId, "pedido", pedidoId, "motoboy_alterado", "Motoboy alterado", $"Agora com {await MotoboyNomeAsync(motoboyId)}");
            return await GetAsync(est, pedidoId);
        }

        public async Task<SimPedidoDetalheDto> AnexarClienteAsync(Guid est, int userId, int pedidoId, Guid clienteId)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            if (StatusOf(row) is StatusPedido.Concluido or StatusPedido.Cancelado)
            {
                throw new DeliveryDomainException(409, "ORDER_FINISHED", "Pedido terminado nao muda de cliente.");
            }

            await using var connection = await _dataSource.OpenConnectionAsync();
            var cliente = await connection.QuerySingleOrDefaultAsync<(string? Nome, string? Tel)?>(@"
SELECT nome, telefone_e164 FROM clientes WHERE id = @Id AND id_estabelecimento = @Est AND ativo = TRUE;",
                new { Id = clienteId, Est = est })
                ?? throw new DeliveryDomainException(404, "CLIENT_NOT_FOUND", "Cliente nao encontrado.");
            var conversaId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
SELECT id FROM conversas WHERE id_cliente = @Id AND id_estabelecimento = @Est ORDER BY data_criacao DESC LIMIT 1;",
                new { Id = clienteId, Est = est });

            await connection.ExecuteAsync(@"
UPDATE pedido SET nome_cliente = COALESCE(NULLIF(@Nome, ''), nome_cliente), telefone_cliente = @Tel,
                  conversa_id = COALESCE(@Conversa, conversa_id)
 WHERE id = @Id AND id_estabelecimento = @Est;", new { Nome = cliente.Nome, Tel = cliente.Tel, Conversa = conversaId, Id = pedidoId, Est = est });

            await LogAsync(est, userId, "pedido", pedidoId, "cliente_anexado", "Cliente anexado", $"Pedido vinculado a {cliente.Nome}");
            return await GetAsync(est, pedidoId);
        }

        public async Task RemoverAsync(Guid est, int userId, int pedidoId)
        {
            var row = await LoadRowAsync(est, pedidoId);
            RequireSimulated(row);
            if (StatusOf(row) is StatusPedido.Atribuido or StatusPedido.EmRota)
            {
                throw new DeliveryDomainException(409, "ORDER_IN_QUEUE", "O pedido esta na fila de um motoboy. Tire-o da fila ou cancele antes de remover.");
            }

            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var p = new { Id = pedidoId, Est = est };
                await connection.ExecuteAsync("DELETE FROM pedido_item WHERE pedido_id = @Id;", p, transaction);
                await connection.ExecuteAsync("DELETE FROM delivery_transfer_requests WHERE pedido_id = @Id;", p, transaction);
                await connection.ExecuteAsync("DELETE FROM pedido_notificacao WHERE pedido_id = @Id;", p, transaction);
                await connection.ExecuteAsync("DELETE FROM delivery_route_stops WHERE pedido_id = @Id;", p, transaction);
                await connection.ExecuteAsync("DELETE FROM pedido WHERE id = @Id AND id_estabelecimento = @Est AND origem = 'simulador';", p, transaction);
                await transaction.CommitAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new DeliveryDomainException(409, "ORDER_HAS_HISTORY", "Este pedido tem historico que impede remover. Cancele-o em vez de remover.");
            }
            await LogAsync(est, userId, "pedido", pedidoId, "pedido_removido", "Pedido removido", "Pedido de teste excluido", "atencao");
        }

        // ------------------------------------------------------------------ apoio

        private static void RequireSimulated(PedidoRow row)
        {
            if (!IsSimulated(row))
            {
                throw new DeliveryDomainException(409, "ORDER_NOT_SIMULATED",
                    "So pedidos de teste (origem simulador) podem ser conduzidos por aqui. Pedidos reais seguem o painel.");
            }
        }

        private async Task ExecAsync(string sql, int pedidoId, Guid est)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new { Id = pedidoId, Est = est });
        }

        private Task LogAsync(Guid est, int userId, string entidade, int refId, string tipo, string titulo, string? detalhe, string status = "sucesso") =>
            LogAsync(est, userId, entidade, refId.ToString(), tipo, titulo, detalhe, status);

        private async Task LogAsync(Guid est, int userId, string entidade, string refId, string tipo, string titulo, string? detalhe, string status = "sucesso")
        {
            try
            {
                await _eventos.AddEventoAsync(est, userId > 0 ? userId : null, null,
                    new SimEventoInput(entidade, refId, tipo, titulo, detalhe, status, null, null));
            }
            catch (PostgresException)
            {
                // Log e melhor esforco: sem a tabela (migration pendente) a acao continua valendo.
            }
        }
    }
}
