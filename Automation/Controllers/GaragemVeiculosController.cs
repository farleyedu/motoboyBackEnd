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
        public async Task<IActionResult> Criar([FromBody] CreateGarageVehicleRequest? request)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Corpo da requisicao e obrigatorio." });
            }

            if (!TryResolveRequiredEstabelecimento(request.IdEstabelecimento, out var effectiveEstabelecimentoId, out var error))
            {
                return error!;
            }

            if (!TryNormalizeCreateRequest(request, out error))
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
                return ValidationError(
                    "Status de veiculo e obrigatorio.",
                    ("status", "Status de veiculo e obrigatorio."));
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

        private bool TryNormalizeCreateRequest(CreateGarageVehicleRequest request, out IActionResult? error)
        {
            error = null;

            request.Titulo = LimparTexto(request.Titulo);
            request.Marca = LimparTexto(request.Marca);
            request.Modelo = LimparTexto(request.Modelo);
            request.Cor = LimparTexto(request.Cor);
            request.Cidade = LimparTexto(request.Cidade);
            request.Carroceria = LimparTexto(request.Carroceria);
            request.Combustivel = LimparTexto(request.Combustivel);
            request.Cambio = LimparTexto(request.Cambio);
            request.Descricao = LimparTexto(request.Descricao);
            request.CodigoEstoque = LimparTexto(request.CodigoEstoque);
            request.Tracao = LimparTexto(request.Tracao);
            request.Placa = LimparTexto(request.Placa);
            request.LabelDestaque = request.Destaque ? LimparTexto(request.LabelDestaque) : null;

            var missingFields = new List<(string Field, string Error)>();
            if (string.IsNullOrWhiteSpace(request.Marca))
            {
                missingFields.Add(("marca", "Marca e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.Modelo))
            {
                missingFields.Add(("modelo", "Modelo e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Categoria))
            {
                missingFields.Add(("categoria", "Categoria e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.TipoVeiculo))
            {
                missingFields.Add(("tipoVeiculo", "Tipo de veiculo e obrigatorio."));
            }

            if (!request.AnoModelo.HasValue)
            {
                missingFields.Add(("anoModelo", "Ano modelo e obrigatorio."));
            }

            if (!request.Preco.HasValue)
            {
                missingFields.Add(("preco", "Preco e obrigatorio."));
            }

            if (missingFields.Count > 0)
            {
                error = ValidationError("Informe marca, modelo, categoria, tipo do veiculo, ano modelo e preco.", missingFields.ToArray());
                return false;
            }

            var maxAno = DateTime.UtcNow.Year + 2;
            var anoModelo = request.AnoModelo ?? 0;
            if (anoModelo < 1900 || anoModelo > maxAno)
            {
                error = ValidationError("Ano modelo invalido.", ("anoModelo", "Ano modelo invalido."));
                return false;
            }

            request.AnoFabricacao ??= request.AnoModelo;
            if (!request.AnoFabricacao.HasValue || request.AnoFabricacao.Value < 1900 || request.AnoFabricacao.Value > maxAno)
            {
                error = ValidationError("Ano de fabricacao invalido.", ("anoFabricacao", "Ano de fabricacao invalido."));
                return false;
            }

            var preco = request.Preco ?? 0m;
            if (preco <= 0)
            {
                error = ValidationError("Preco do veiculo deve ser maior que zero.", ("preco", "Preco do veiculo deve ser maior que zero."));
                return false;
            }

            if (request.PrecoAnterior.HasValue && request.PrecoAnterior.Value < 0)
            {
                error = ValidationError("Preco anterior invalido.", ("precoAnterior", "Preco anterior invalido."));
                return false;
            }

            if (request.Km.HasValue && request.Km.Value < 0)
            {
                error = ValidationError("Km do veiculo deve ser maior ou igual a zero.", ("km", "Km do veiculo deve ser maior ou igual a zero."));
                return false;
            }

            if (request.Portas.HasValue && request.Portas.Value <= 0)
            {
                error = ValidationError("Quantidade de portas invalida.", ("portas", "Quantidade de portas invalida."));
                return false;
            }

            if (request.Assentos.HasValue && request.Assentos.Value <= 0)
            {
                error = ValidationError("Quantidade de assentos invalida.", ("assentos", "Quantidade de assentos invalida."));
                return false;
            }

            if (!TryNormalizeCategory(request.Categoria, allowTodos: false, out var categoriaNormalizada, out error) || categoriaNormalizada == null)
            {
                return false;
            }

            if (!TryNormalizeVehicleCondition(request.TipoVeiculo, out var tipoVeiculoNormalizado, out error))
            {
                return false;
            }

            var statusInformado = string.IsNullOrWhiteSpace(request.Status) ? "disponivel" : request.Status;
            if (!TryNormalizeStatus(statusInformado, out var statusNormalizado, out error) || statusNormalizado == null)
            {
                return false;
            }

            if (tipoVeiculoNormalizado == "seminovo" && !request.Km.HasValue)
            {
                error = ValidationError("Km e obrigatorio para veiculos seminovos.", ("km", "Km e obrigatorio para veiculos seminovos."));
                return false;
            }

            request.Categoria = categoriaNormalizada;
            request.TipoVeiculo = tipoVeiculoNormalizado;
            request.Status = statusNormalizado;
            request.Titulo ??= $"{request.Marca} {request.Modelo} {anoModelo.ToString(CultureInfo.InvariantCulture)}";

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
                    error = ValidationError(
                        "Cada item de condicao precisa informar o nome do item.",
                        ("condicoes", "Cada item de condicao precisa informar o nome do item."));
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

            var missingFields = new List<(string Field, string Error)>();
            if (string.IsNullOrWhiteSpace(request.Titulo))
            {
                missingFields.Add(("titulo", "Titulo e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Marca))
            {
                missingFields.Add(("marca", "Marca e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.Modelo))
            {
                missingFields.Add(("modelo", "Modelo e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Cor))
            {
                missingFields.Add(("cor", "Cor e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.Cidade))
            {
                missingFields.Add(("cidade", "Cidade e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.Carroceria))
            {
                missingFields.Add(("carroceria", "Carroceria e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.Categoria))
            {
                missingFields.Add(("categoria", "Categoria e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.TipoVeiculo))
            {
                missingFields.Add(("tipoVeiculo", "Tipo de veiculo e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Status))
            {
                missingFields.Add(("status", "Status e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Combustivel))
            {
                missingFields.Add(("combustivel", "Combustivel e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Cambio))
            {
                missingFields.Add(("cambio", "Cambio e obrigatorio."));
            }

            if (string.IsNullOrWhiteSpace(request.Descricao))
            {
                missingFields.Add(("descricao", "Descricao e obrigatoria."));
            }

            if (string.IsNullOrWhiteSpace(request.CodigoEstoque))
            {
                missingFields.Add(("codigoEstoque", "Codigo de estoque e obrigatorio."));
            }

            if (missingFields.Count > 0)
            {
                error = ValidationError("Preencha os campos obrigatorios do veiculo.", missingFields.ToArray());
                return false;
            }

            var maxAno = DateTime.UtcNow.Year + 2;
            var yearErrors = new List<(string Field, string Error)>();
            if (request.AnoFabricacao < 1900 || request.AnoFabricacao > maxAno)
            {
                yearErrors.Add(("anoFabricacao", "Ano de fabricacao invalido."));
            }

            if (request.AnoModelo < 1900 || request.AnoModelo > maxAno)
            {
                yearErrors.Add(("anoModelo", "Ano modelo invalido."));
            }

            if (yearErrors.Count > 0)
            {
                error = ValidationError("Ano de fabricacao ou ano modelo invalido.", yearErrors.ToArray());
                return false;
            }

            var numericErrors = new List<(string Field, string Error)>();
            if (request.Preco <= 0)
            {
                numericErrors.Add(("preco", "Preco deve ser maior que zero."));
            }

            if (request.Km < 0)
            {
                numericErrors.Add(("km", "Km deve ser maior ou igual a zero."));
            }

            if (request.Portas <= 0)
            {
                numericErrors.Add(("portas", "Quantidade de portas invalida."));
            }

            if (request.Assentos <= 0)
            {
                numericErrors.Add(("assentos", "Quantidade de assentos invalida."));
            }

            if (numericErrors.Count > 0)
            {
                error = ValidationError("Preco e dados estruturais do veiculo devem ser validos.", numericErrors.ToArray());
                return false;
            }

            if (request.PrecoAnterior.HasValue && request.PrecoAnterior.Value < 0)
            {
                error = ValidationError("Preco anterior invalido.", ("precoAnterior", "Preco anterior invalido."));
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
                    error = ValidationError(
                        "Cada item de condicao precisa informar o nome do item.",
                        ("condicoes", "Cada item de condicao precisa informar o nome do item."));
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

        private IActionResult ValidationError(string message, params (string Field, string Error)[] fieldErrors)
        {
            var errors = (fieldErrors ?? Array.Empty<(string Field, string Error)>())
                .Where(item => !string.IsNullOrWhiteSpace(item.Field) && !string.IsNullOrWhiteSpace(item.Error))
                .GroupBy(item => item.Field, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Error).Distinct().ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            return UnprocessableEntity(new
            {
                success = false,
                error = message,
                errors
            });
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

            error = ValidationError("Categoria de veiculo invalida.", ("categoria", "Categoria de veiculo invalida."));
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

            error = ValidationError("Status de veiculo invalido.", ("status", "Status de veiculo invalido."));
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

            error = ValidationError("Tipo de veiculo invalido.", ("tipoVeiculo", "Tipo de veiculo invalido."));
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

            error = ValidationError("Status de condicao invalido.", ("condicoes", "Status de condicao invalido."));
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
