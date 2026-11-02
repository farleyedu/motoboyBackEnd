using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Fases;
using APIBack.DTOs.CRM;
using R = APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras;

namespace APIBack.Acompanhamento.Relacionamento
{
    public sealed class LeadCandidatoBruto
    {
        public Guid Id { get; set; }
        /// <summary>lead | loja</summary>
        public string Tipo { get; set; } = "lead";
        public string Nome { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public string? ResponsavelNome { get; set; }
        public string Situacao { get; set; } = string.Empty;
    }

    public sealed class LeadNovo
    {
        public string Nome { get; set; } = string.Empty;
        public string NomeNormalizado { get; set; } = string.Empty;
        public string Whatsapp { get; set; } = string.Empty;
        public string Origem { get; set; } = string.Empty;
        public string Decisor { get; set; } = string.Empty;
        public string? Cnpj { get; set; }
        public string? Email { get; set; }
        public int ResponsavelUsuarioId { get; set; }
        public string PassoTipo { get; set; } = string.Empty;
        public DateOnly PassoData { get; set; }
        public string? PassoNota { get; set; }
        public string? Chave { get; set; }
        public string Hash { get; set; } = string.Empty;
        public int UsuarioId { get; set; }
        /// <summary>Quando a pessoa decidiu criar mesmo com parecidos: motivo e quais candidatos viu. Fica no historico do lead.</summary>
        public string? DecisaoDuplicidade { get; set; }
    }

    public sealed class LeadGravado
    {
        public Guid OportunidadeId { get; set; }
        public Guid AcompanhamentoId { get; set; }
        public Guid ProximoPassoId { get; set; }
        public string? Hash { get; set; }
        /// <summary>A chave de idempotencia ja existia: nada foi criado agora.</summary>
        public bool Repetida { get; set; }
    }

    public interface ILeadStore
    {
        Task<UsuarioEquipeDto?> MembroDaEquipeAsync(int usuarioId, CancellationToken ct);
        Task<IReadOnlyList<LeadCandidatoBruto>> CandidatosAsync(string nomeNormalizado, string? e164, CancellationToken ct);
        Task<LeadGravado?> LeadPorChaveAsync(string chave, CancellationToken ct);
        Task<LeadGravado> CriarLeadAsync(LeadNovo novo, CancellationToken ct);
    }

    public sealed record CnpjDados(string RazaoSocial, string? NomeFantasia, string? Situacao);

    /// <summary>Fonte real de consulta de CNPJ. Hoje nenhuma esta configurada: sem implementacao registrada o lead nao e enriquecido.</summary>
    public interface ICnpjFonte
    {
        bool Disponivel { get; }
        Task<CnpjDados?> ConsultarAsync(string cnpj, CancellationToken ct);
    }

    /// <summary>
    /// Novo lead: nome, WhatsApp E.164, origem, decisor, responsavel real e proximo passo obrigatorios. Nome sem acento ou telefone iguais
    /// devolvem candidatos (409) e so criam com decisao humana explicita; nada e consolidado em silencio. Idempotente por chave.
    /// </summary>
    public sealed class LeadCriador
    {
        public const string DecisaoCriarNovo = "criar_novo";

        private readonly ILeadStore _store;
        private readonly TimeProvider _clock;
        private readonly ICnpjFonte? _cnpj;

        public LeadCriador(ILeadStore store, TimeProvider? clock = null, ICnpjFonte? cnpj = null)
        {
            _store = store;
            _clock = clock ?? TimeProvider.System;
            _cnpj = cnpj;
        }

        public async Task<IReadOnlyList<CrmLeadCandidatoDto>> ParecidosAsync(string? nome, string? telefone, CancellationToken ct = default)
        {
            var normalizado = R.NormalizarNome(nome);
            var e164 = string.IsNullOrWhiteSpace(telefone) ? null : R.TelefoneE164(telefone)
                ?? throw new RelacionamentoException(422, "WHATSAPP_INVALIDO", "Informe o WhatsApp em E.164, com + e codigo do pais (ex.: +5534991480112).");
            if (normalizado.Length == 0 && e164 == null)
                throw new RelacionamentoException(422, "BUSCA_VAZIA", "Informe o nome ou o telefone para procurar parecidos.");
            return Classificar(await _store.CandidatosAsync(normalizado, e164, ct), nome, e164);
        }

        public static IReadOnlyList<CrmLeadCandidatoDto> Classificar(IEnumerable<LeadCandidatoBruto> brutos, string? nome, string? e164)
        {
            var lista = new List<CrmLeadCandidatoDto>();
            foreach (var b in brutos)
            {
                var motivos = new List<string>();
                if (e164 != null && R.MesmoTelefone(e164, b.Telefone)) motivos.Add("telefone_igual");
                var sim = R.Similaridade(nome, b.Nome);
                if (sim >= 1) motivos.Add("nome_igual");
                else if (sim >= R.LimiarParecido) motivos.Add("nome_parecido");
                if (motivos.Count == 0) continue;
                lista.Add(new CrmLeadCandidatoDto
                {
                    Id = b.Id, Tipo = b.Tipo, Nome = b.Nome, Telefone = b.Telefone, ResponsavelUsuarioId = b.ResponsavelUsuarioId,
                    ResponsavelNome = b.ResponsavelNome, Situacao = b.Situacao, Motivos = motivos, Similaridade = Math.Round(sim, 2)
                });
            }
            return lista.OrderByDescending(c => c.Motivos.Contains("telefone_igual")).ThenByDescending(c => c.Similaridade).ThenBy(c => c.Nome).ToList();
        }

        public async Task<CrmLeadCriadoDto> CriarAsync(CriarCrmLeadRequest request, int usuarioId, string? chave, CancellationToken ct = default)
        {
            var hoje = R.Hoje(_clock.GetUtcNow());
            R.ValidarProximoPasso(request.ProximoPasso?.Tipo, request.ProximoPasso?.Data, hoje);

            var erros = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Erro(string campo, string msg) => erros[campo] = new List<string> { msg };
            var nome = request.Nome?.Trim() ?? string.Empty;
            var normalizado = R.NormalizarNome(nome);
            if (normalizado.Length == 0) Erro("nome", "Informe o nome do lead.");
            var whatsapp = R.TelefoneE164(request.Whatsapp);
            if (whatsapp == null) Erro("whatsapp", "Informe o WhatsApp em E.164, com + e codigo do pais (ex.: +5534991480112).");
            var origem = request.Origem?.Trim() ?? string.Empty;
            if (origem.Length == 0) Erro("origem", "Informe a origem do lead.");
            var decisor = request.Decisor?.Trim() ?? string.Empty;
            if (decisor.Length == 0) Erro("decisor", "Informe quem decide.");
            string? cnpj = null;
            if (!string.IsNullOrWhiteSpace(request.Cnpj))
            {
                cnpj = DocumentoRegras.Digitos(request.Cnpj);
                if (cnpj is not { Length: 14 } || !DocumentoRegras.Valido(cnpj)) Erro("cnpj", "CNPJ invalido. Se nao souber, deixe em branco.");
            }
            UsuarioEquipeDto? responsavel = null;
            if (!request.ResponsavelUsuarioId.HasValue) Erro("responsavelUsuarioId", "Informe o responsavel.");
            else
            {
                responsavel = await _store.MembroDaEquipeAsync(request.ResponsavelUsuarioId.Value, ct);
                if (responsavel == null) Erro("responsavelUsuarioId", "O responsavel precisa ser alguem da equipe Zippy.");
            }
            if (erros.Count > 0) throw new RelacionamentoException(422, "VALIDACAO", "Dados invalidos.", erros);

            var passoTipo = request.ProximoPasso!.Tipo!.Trim().ToLowerInvariant();
            var passoData = request.ProximoPasso.Data!.Value;
            var hash = Hash(normalizado, whatsapp!, origem, decisor, cnpj, responsavel!.Id, passoTipo, passoData);

            if (!string.IsNullOrWhiteSpace(chave))
            {
                var existente = await _store.LeadPorChaveAsync(chave.Trim(), ct);
                if (existente != null) return Repeticao(existente, hash, request, whatsapp!, responsavel, passoTipo, passoData);
            }

            var candidatos = Classificar(await _store.CandidatosAsync(normalizado, whatsapp, ct), nome, whatsapp);
            string? decisao = null;
            if (candidatos.Count > 0)
            {
                if (request.DecisaoDuplicidade != DecisaoCriarNovo)
                    throw Duplicado(candidatos, "Ja existe cadastro parecido. Confira os candidatos e decida se e o mesmo.");
                if (string.IsNullOrWhiteSpace(request.MotivoDuplicidade) || request.MotivoDuplicidade.Trim().Length < R.MotivoMinimo)
                    throw new RelacionamentoException(422, "MOTIVO_DUPLICIDADE", "Explique por que este lead e novo mesmo com cadastros parecidos.");
                var vistos = (request.CandidatosConfirmados ?? new()).ToHashSet();
                if (candidatos.Any(c => !vistos.Contains(c.Id)))
                    throw Duplicado(candidatos, "Surgiram candidatos que voce ainda nao conferiu. Confira a lista atualizada.");
                decisao = JsonSerializer.Serialize(new { decisao = DecisaoCriarNovo, motivo = request.MotivoDuplicidade.Trim(), candidatos = candidatos.Select(c => c.Id) });
            }

            var gravado = await _store.CriarLeadAsync(new LeadNovo
            {
                Nome = nome, NomeNormalizado = normalizado, Whatsapp = whatsapp!, Origem = origem, Decisor = decisor, Cnpj = cnpj,
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(), ResponsavelUsuarioId = responsavel.Id,
                PassoTipo = passoTipo, PassoData = passoData, PassoNota = string.IsNullOrWhiteSpace(request.ProximoPasso.Nota) ? null : request.ProximoPasso.Nota.Trim(),
                Chave = string.IsNullOrWhiteSpace(chave) ? null : chave.Trim(), Hash = hash, UsuarioId = usuarioId, DecisaoDuplicidade = decisao
            }, ct);

            if (gravado.Repetida) return Repeticao(gravado, hash, request, whatsapp!, responsavel, passoTipo, passoData);
            return Montar(gravado, whatsapp!, responsavel, passoTipo, passoData, request.ProximoPasso.Nota, false, decisao != null, await EnriquecerAsync(cnpj, ct));
        }

        private CrmLeadCriadoDto Repeticao(LeadGravado existente, string hash, CriarCrmLeadRequest request, string whatsapp, UsuarioEquipeDto responsavel, string tipo, DateOnly data)
        {
            if (existente.Hash != hash)
                throw new RelacionamentoException(409, "CHAVE_REUTILIZADA", "Esta chave de idempotencia ja criou um lead com outros dados.");
            return Montar(existente, whatsapp, responsavel, tipo, data, request.ProximoPasso?.Nota, true, false,
                new CrmLeadEnriquecimentoDto { Solicitado = !string.IsNullOrWhiteSpace(request.Cnpj), Disponivel = false, Motivo = "Lead ja criado; sem nova consulta." });
        }

        private static RelacionamentoException Duplicado(IReadOnlyList<CrmLeadCandidatoDto> candidatos, string mensagem) =>
            new(409, "DUPLICADO", mensagem, new { candidatos, decisoes = new[] { DecisaoCriarNovo } });

        private CrmLeadCriadoDto Montar(LeadGravado g, string whatsapp, UsuarioEquipeDto responsavel, string tipo, DateOnly data, string? nota, bool repetida, bool decidida,
            CrmLeadEnriquecimentoDto enriquecimento) => new()
        {
            OportunidadeId = g.OportunidadeId, AcompanhamentoId = g.AcompanhamentoId, WhatsappE164 = whatsapp, Responsavel = responsavel,
            ProximoPasso = new ProximoPassoDto(g.ProximoPassoId, tipo, data, nota, null, _clock.GetUtcNow(), false),
            Enriquecimento = enriquecimento, Repetida = repetida, DuplicidadeDecidida = decidida
        };

        private async Task<CrmLeadEnriquecimentoDto> EnriquecerAsync(string? cnpj, CancellationToken ct)
        {
            if (cnpj == null) return new CrmLeadEnriquecimentoDto { Solicitado = false, Disponivel = _cnpj?.Disponivel == true };
            if (_cnpj is not { Disponivel: true })
                return new CrmLeadEnriquecimentoDto { Solicitado = true, Disponivel = false, Motivo = "Nenhuma fonte de consulta de CNPJ esta configurada; o CNPJ foi guardado sem enriquecer." };
            try
            {
                var dados = await _cnpj.ConsultarAsync(cnpj, ct);
                return dados == null
                    ? new CrmLeadEnriquecimentoDto { Solicitado = true, Disponivel = true, Motivo = "CNPJ nao encontrado na fonte." }
                    : new CrmLeadEnriquecimentoDto { Solicitado = true, Disponivel = true, RazaoSocial = dados.RazaoSocial, NomeFantasia = dados.NomeFantasia, Situacao = dados.Situacao };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new CrmLeadEnriquecimentoDto { Solicitado = true, Disponivel = false, Motivo = "A consulta de CNPJ falhou; o lead foi criado sem enriquecer." };
            }
        }

        private static string Hash(string nome, string whatsapp, string origem, string decisor, string? cnpj, int responsavel, string tipo, DateOnly data)
        {
            var texto = string.Join("|", nome, whatsapp, R.NormalizarNome(origem), R.NormalizarNome(decisor), cnpj ?? "", responsavel, tipo, data.ToString("yyyy-MM-dd"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();
        }
    }
}
