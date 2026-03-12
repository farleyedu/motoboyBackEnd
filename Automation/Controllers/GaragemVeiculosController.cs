using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("api/garagem")]
    public class GaragemVeiculosController : ControllerBase
    {
        private static readonly Dictionary<string, string> CategoriasPermitidas = new(StringComparer.OrdinalIgnoreCase)
        {
            ["suv"] = "SUV",
            ["sedan"] = "Sedan",
            ["pickup"] = "Pickup",
            ["eletrico"] = "Eletrico",
            ["hatch"] = "Hatch"
        };

        private static readonly Dictionary<string, string> StatusPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            ["disponivel"] = "disponivel",
            ["indisponivel"] = "indisponivel",
            ["vendido"] = "vendido"
        };

        private static readonly Dictionary<string, string> CondicoesVeiculoPermitidas = new(StringComparer.OrdinalIgnoreCase)
        {
            ["novo"] = "novo",
            ["seminovo"] = "seminovo"
        };

        private static readonly Dictionary<string, string> StatusCondicaoPermitidos = new(StringComparer.OrdinalIgnoreCase)
        {
            ["excelente"] = "Excelente",
            ["bom"] = "Bom",
            ["regular"] = "Regular"
        };

        private readonly IGaragemVeiculoRepository _repository;

        public GaragemVeiculosController(IGaragemVeiculoRepository repository)
        {
            _repository = repository;
        }

        [HttpGet("veiculos")]
        [RequirePermission("Garagem", "visualizar")]
        public async Task<IActionResult> Listar(
            [FromQuery] Guid? estabelecimentoId = null,
            [FromQuery] string? status = null,
            [FromQuery] string? categoria = null,
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (!TryResolveRequiredEstabelecimento(estabelecimentoId, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            if (!TryNormalizeStatus(status, out var statusNormalizado, out error))
            {
                return error!;
            }

            if (!TryNormalizeCategory(categoria, allowTodos: true, out var categoriaNormalizada, out error))
            {
                return error!;
            }

            var pageNormalizado = page < 1 ? 1 : page;
            var pageSizeNormalizado = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 200);

            var (items, total) = await _repository.ListarAsync(
                effectiveEstabelecimentoId,
                statusNormalizado,
                categoriaNormalizada,
                LimparTexto(search),
                pageNormalizado,
                pageSizeNormalizado);

            return Ok(new GarageVehicleListResponseDto
            {
                Items = items,
                Total = total,
                Page = pageNormalizado,
                PageSize = pageSizeNormalizado
            });
        }

        [HttpGet("veiculos/{id:guid}")]
        [RequirePermission("Garagem", "visualizar")]
        public async Task<IActionResult> Obter(Guid id, [FromQuery] Guid? estabelecimentoId = null)
        {
            if (!TryResolveOptionalEstabelecimento(estabelecimentoId, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            var item = await _repository.ObterPorIdAsync(id, effectiveEstabelecimentoId);
            return item == null
                ? NotFound(new { success = false, error = "Veiculo nao encontrado." })
                : Ok(item);
        }

        [HttpPost("veiculos")]
        [RequirePermission("Garagem", "cadastrar")]
        public async Task<IActionResult> Criar([FromBody] UpsertGarageVehicleRequest? request)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!TryResolveRequiredEstabelecimento(request.IdEstabelecimento, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            if (!TryNormalizeUpsertRequest(request, out error))
            {
                return error!;
            }

            try
            {
                var created = await _repository.CriarAsync(effectiveEstabelecimentoId, request);
                return CreatedAtAction(nameof(Obter), new { id = created.Id, estabelecimentoId = effectiveEstabelecimentoId }, created);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { success = false, error = ex.Message });
            }
        }

        [HttpPut("veiculos/{id:guid}")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> Atualizar(Guid id, [FromBody] UpsertGarageVehicleRequest? request)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!TryResolveRequiredEstabelecimento(request.IdEstabelecimento, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            if (!TryNormalizeUpsertRequest(request, out error))
            {
                return error!;
            }

            try
            {
                var updated = await _repository.AtualizarAsync(id, effectiveEstabelecimentoId, request);
                return updated == null
                    ? NotFound(new { success = false, error = "Veiculo nao encontrado." })
                    : Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { success = false, error = ex.Message });
            }
        }

        [HttpDelete("veiculos/{id:guid}")]
        [RequirePermission("Garagem", "excluir")]
        public async Task<IActionResult> Remover(Guid id, [FromQuery] Guid? estabelecimentoId = null)
        {
            if (!TryResolveOptionalEstabelecimento(estabelecimentoId, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            var removed = await _repository.RemoverAsync(id, effectiveEstabelecimentoId);
            return removed
                ? NoContent()
                : NotFound(new { success = false, error = "Veiculo nao encontrado." });
        }

        [HttpPatch("veiculos/{id:guid}/status")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> AtualizarStatus(
            Guid id,
            [FromBody] UpdateGarageVehicleStatusRequest? request,
            [FromQuery] Guid? estabelecimentoId = null)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!TryResolveOptionalEstabelecimento(estabelecimentoId, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            if (string.IsNullOrWhiteSpace(request.Status))
            {
                return UnprocessableEntity(new { success = false, error = "Status de veiculo e obrigatorio." });
            }

            if (!TryNormalizeStatus(request.Status, out var statusNormalizado, out error) || statusNormalizado == null)
            {
                return error!;
            }

            var updated = await _repository.AtualizarStatusAsync(id, statusNormalizado, effectiveEstabelecimentoId);
            return updated
                ? Ok()
                : NotFound(new { success = false, error = "Veiculo nao encontrado." });
        }

        [HttpPatch("veiculos/{id:guid}/destaque")]
        [RequirePermission("Garagem", "editar")]
        public async Task<IActionResult> AtualizarDestaque(
            Guid id,
            [FromBody] UpdateGarageVehicleHighlightRequest? request,
            [FromQuery] Guid? estabelecimentoId = null)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!TryResolveOptionalEstabelecimento(estabelecimentoId, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            var label = request.Destaque ? LimparTexto(request.Label) : null;
            var updated = await _repository.AtualizarDestaqueAsync(id, request.Destaque, label, effectiveEstabelecimentoId);
            return updated
                ? Ok()
                : NotFound(new { success = false, error = "Veiculo nao encontrado." });
        }

        [HttpGet("vitrine")]
        [AllowAnonymous]
        public async Task<IActionResult> ObterVitrine(
            [FromQuery] Guid estabelecimentoId,
            [FromQuery] string? categoria = null,
            [FromQuery] string? search = null)
        {
            if (estabelecimentoId == Guid.Empty)
            {
                return BadRequest(new { success = false, error = "Informe um estabelecimento valido." });
            }

            if (!TryNormalizeCategory(categoria, allowTodos: true, out var categoriaNormalizada, out var error))
            {
                return error!;
            }

            var vitrine = await _repository.ObterVitrineAsync(estabelecimentoId, categoriaNormalizada, LimparTexto(search));
            return Ok(vitrine);
        }

        [HttpGet("metricas")]
        [RequirePermission("Garagem", "visualizar")]
        public async Task<IActionResult> ObterMetricas([FromQuery] Guid? estabelecimentoId = null)
        {
            if (!TryResolveRequiredEstabelecimento(estabelecimentoId, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            var metrics = await _repository.ObterMetricasAsync(effectiveEstabelecimentoId);
            return Ok(metrics);
        }

        private bool TryNormalizeUpsertRequest(UpsertGarageVehicleRequest request, out IActionResult? error)
        {
            error = null;

            request.Titulo = LimparTexto(request.Titulo) ?? string.Empty;
            request.Marca = LimparTexto(request.Marca) ?? string.Empty;
            request.Modelo = LimparTexto(request.Modelo) ?? string.Empty;
            request.Cor = LimparTexto(request.Cor) ?? string.Empty;
            request.Cidade = LimparTexto(request.Cidade) ?? string.Empty;
            request.Carroceria = LimparTexto(request.Carroceria) ?? string.Empty;
            request.Combustivel = LimparTexto(request.Combustivel) ?? string.Empty;
            request.Cambio = LimparTexto(request.Cambio) ?? string.Empty;
            request.Descricao = LimparTexto(request.Descricao) ?? string.Empty;
            request.CodigoEstoque = LimparTexto(request.CodigoEstoque) ?? string.Empty;
            request.Tracao = LimparTexto(request.Tracao);
            request.Placa = LimparTexto(request.Placa);
            request.LabelDestaque = request.Destaque ? LimparTexto(request.LabelDestaque) : null;
            request.Slug = null;

            if (string.IsNullOrWhiteSpace(request.Titulo) ||
                string.IsNullOrWhiteSpace(request.Marca) ||
                string.IsNullOrWhiteSpace(request.Modelo) ||
                string.IsNullOrWhiteSpace(request.Cor) ||
                string.IsNullOrWhiteSpace(request.Cidade) ||
                string.IsNullOrWhiteSpace(request.Carroceria) ||
                string.IsNullOrWhiteSpace(request.Categoria) ||
                string.IsNullOrWhiteSpace(request.TipoVeiculo) ||
                string.IsNullOrWhiteSpace(request.Status) ||
                string.IsNullOrWhiteSpace(request.Combustivel) ||
                string.IsNullOrWhiteSpace(request.Cambio) ||
                string.IsNullOrWhiteSpace(request.Descricao) ||
                string.IsNullOrWhiteSpace(request.CodigoEstoque))
            {
                error = UnprocessableEntity(new { success = false, error = "Preencha os campos obrigatorios do veiculo." });
                return false;
            }

            var maxAno = DateTime.UtcNow.Year + 2;
            if (request.AnoFabricacao < 1900 || request.AnoFabricacao > maxAno ||
                request.AnoModelo < 1900 || request.AnoModelo > maxAno)
            {
                error = UnprocessableEntity(new { success = false, error = "Ano de fabricacao ou ano modelo invalido." });
                return false;
            }

            if (request.Preco <= 0 || request.Km < 0 || request.Portas <= 0 || request.Assentos <= 0)
            {
                error = UnprocessableEntity(new { success = false, error = "Preco e dados estruturais do veiculo devem ser validos." });
                return false;
            }

            if (request.PrecoAnterior.HasValue && request.PrecoAnterior.Value < 0)
            {
                error = UnprocessableEntity(new { success = false, error = "Preco anterior invalido." });
                return false;
            }

            if (!TryNormalizeCategory(request.Categoria, allowTodos: false, out var categoriaNormalizada, out error) || categoriaNormalizada == null)
            {
                return false;
            }

            if (!TryNormalizeStatus(request.Status, out var statusNormalizado, out error) || statusNormalizado == null)
            {
                return false;
            }

            if (!TryNormalizeVehicleCondition(request.TipoVeiculo, out var tipoVeiculoNormalizado, out error))
            {
                return false;
            }

            request.Categoria = categoriaNormalizada;
            request.Status = statusNormalizado;
            request.TipoVeiculo = tipoVeiculoNormalizado;

            request.Opcionais = (request.Opcionais ?? new List<string>())
                .Select(LimparTexto)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();

            request.Fotos = (request.Fotos ?? new List<string>())
                .Select(LimparTexto)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();

            var condicoes = new List<GarageVehicleConditionRequestDto>();
            foreach (var item in request.Condicoes ?? new List<GarageVehicleConditionRequestDto>())
            {
                var itemNormalizado = LimparTexto(item.Item);
                if (string.IsNullOrWhiteSpace(itemNormalizado))
                {
                    error = UnprocessableEntity(new { success = false, error = "Cada item de condicao precisa informar o nome do item." });
                    return false;
                }

                if (!TryNormalizeConditionStatus(item.Status, out var statusCondicaoNormalizado, out error))
                {
                    return false;
                }

                condicoes.Add(new GarageVehicleConditionRequestDto
                {
                    Item = itemNormalizado,
                    Status = statusCondicaoNormalizado,
                    Nota = LimparTexto(item.Nota) ?? string.Empty
                });
            }

            request.Condicoes = condicoes;
            return true;
        }

        private bool TryNormalizeCategory(string? value, bool allowTodos, out string? normalized, out IActionResult? error)
        {
            error = null;
            normalized = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var token = NormalizeToken(value);
            if (allowTodos && token == "todos")
            {
                return true;
            }

            if (CategoriasPermitidas.TryGetValue(token, out var mapped))
            {
                normalized = mapped;
                return true;
            }

            error = UnprocessableEntity(new { success = false, error = "Categoria de veiculo invalida." });
            return false;
        }

        private bool TryNormalizeStatus(string? value, out string? normalized, out IActionResult? error)
        {
            error = null;
            normalized = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var token = NormalizeToken(value);
            if (StatusPermitidos.TryGetValue(token, out var mapped))
            {
                normalized = mapped;
                return true;
            }

            error = UnprocessableEntity(new { success = false, error = "Status de veiculo invalido." });
            return false;
        }

        private bool TryNormalizeVehicleCondition(string? value, out string normalized, out IActionResult? error)
        {
            error = null;
            normalized = string.Empty;

            var token = NormalizeToken(value);
            if (CondicoesVeiculoPermitidas.TryGetValue(token, out var mapped))
            {
                normalized = mapped;
                return true;
            }

            error = UnprocessableEntity(new { success = false, error = "Tipo de veiculo invalido." });
            return false;
        }

        private bool TryNormalizeConditionStatus(string? value, out string normalized, out IActionResult? error)
        {
            error = null;
            normalized = string.Empty;

            var token = NormalizeToken(value);
            if (StatusCondicaoPermitidos.TryGetValue(token, out var mapped))
            {
                normalized = mapped;
                return true;
            }

            error = UnprocessableEntity(new { success = false, error = "Status de condicao invalido." });
            return false;
        }

        private bool TryResolveRequiredEstabelecimento(Guid? requestedId, out Guid effectiveId, out IActionResult? error)
        {
            var contextoId = HttpContext.GetEstabelecimentoId();
            var isSuperAdmin = HttpContext.IsSuperAdmin();

            if (isSuperAdmin)
            {
                if (requestedId.HasValue && requestedId.Value != Guid.Empty)
                {
                    effectiveId = requestedId.Value;
                    error = null;
                    return true;
                }

                if (contextoId.HasValue && contextoId.Value != Guid.Empty)
                {
                    effectiveId = contextoId.Value;
                    error = null;
                    return true;
                }

                effectiveId = Guid.Empty;
                error = BadRequest(new { success = false, error = "Informe um estabelecimento valido." });
                return false;
            }

            if (!contextoId.HasValue || contextoId.Value == Guid.Empty)
            {
                effectiveId = Guid.Empty;
                error = BadRequest(new { success = false, error = "Selecione um estabelecimento para continuar." });
                return false;
            }

            if (requestedId.HasValue && requestedId.Value != Guid.Empty && requestedId.Value != contextoId.Value)
            {
                effectiveId = Guid.Empty;
                error = StatusCode(403, new { success = false, error = "Acesso negado ao estabelecimento informado." });
                return false;
            }

            effectiveId = contextoId.Value;
            error = null;
            return true;
        }

        private bool TryResolveOptionalEstabelecimento(Guid? requestedId, out Guid? effectiveId, out IActionResult? error)
        {
            var contextoId = HttpContext.GetEstabelecimentoId();
            var isSuperAdmin = HttpContext.IsSuperAdmin();

            if (isSuperAdmin)
            {
                if (requestedId.HasValue && requestedId.Value != Guid.Empty)
                {
                    effectiveId = requestedId.Value;
                    error = null;
                    return true;
                }

                if (contextoId.HasValue && contextoId.Value != Guid.Empty)
                {
                    effectiveId = contextoId.Value;
                    error = null;
                    return true;
                }

                effectiveId = null;
                error = null;
                return true;
            }

            if (!contextoId.HasValue || contextoId.Value == Guid.Empty)
            {
                effectiveId = null;
                error = BadRequest(new { success = false, error = "Selecione um estabelecimento para continuar." });
                return false;
            }

            if (requestedId.HasValue && requestedId.Value != Guid.Empty && requestedId.Value != contextoId.Value)
            {
                effectiveId = null;
                error = StatusCode(403, new { success = false, error = "Acesso negado ao estabelecimento informado." });
                return false;
            }

            effectiveId = contextoId.Value;
            error = null;
            return true;
        }

        private static string? LimparTexto(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
