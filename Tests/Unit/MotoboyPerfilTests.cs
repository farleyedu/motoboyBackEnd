using System;
using APIBack.DTOs.Motoboy;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class MotoboyPerfilTests
    {
        private static AtualizarMotoboyPerfilRequest Valid() => new()
        {
            Nome = "  Joao Motoboy ",
            Telefone = "(34) 99999-1234",
            Cpf = "529.982.247-25", // CPF valido conhecido (digitos verificadores corretos)
            DataNascimento = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-25)),
            TipoVeiculo = "Moto",
            PixTipo = "CPF",
            PixChave = "52998224725",
            StatusCadastro = "ativo"
        };

        [Fact]
        public void BuildCommand_TrimsNormalizesAndValidatesCpf()
        {
            var command = GestaoService.BuildMotoboyPerfilCommand(Valid());

            Assert.Equal("Joao Motoboy", command.Nome);
            Assert.Equal("52998224725", command.Cpf);
            Assert.Equal("moto", command.TipoVeiculo);
            Assert.Equal("cpf", command.PixTipo);
            Assert.Equal("ativo", command.StatusCadastro);
        }

        [Fact]
        public void BuildCommand_RejectsInvalidCpf()
        {
            var request = Valid();
            request.Cpf = "111.111.111-11"; // todos os digitos iguais: invalido
            var ex = Assert.Throws<RequestValidationException>(() => GestaoService.BuildMotoboyPerfilCommand(request));
            Assert.True(ex.Errors.ContainsKey("cpf"));
        }

        [Fact]
        public void BuildCommand_RejectsUnderageMotoboy()
        {
            var request = Valid();
            request.DataNascimento = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-16));
            var ex = Assert.Throws<RequestValidationException>(() => GestaoService.BuildMotoboyPerfilCommand(request));
            Assert.True(ex.Errors.ContainsKey("dataNascimento"));
        }

        [Fact]
        public void BuildCommand_RejectsFutureBirthDate()
        {
            var request = Valid();
            request.DataNascimento = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
            var ex = Assert.Throws<RequestValidationException>(() => GestaoService.BuildMotoboyPerfilCommand(request));
            Assert.True(ex.Errors.ContainsKey("dataNascimento"));
        }

        [Theory]
        [InlineData("carro")]
        [InlineData("bicicleta")]
        [InlineData("a_pe")]
        public void BuildCommand_AcceptsKnownVehicleTypes(string tipo)
        {
            var request = Valid();
            request.TipoVeiculo = tipo;
            var command = GestaoService.BuildMotoboyPerfilCommand(request);
            Assert.Equal(tipo, command.TipoVeiculo);
        }

        [Fact]
        public void BuildCommand_RejectsUnknownVehicleType()
        {
            var request = Valid();
            request.TipoVeiculo = "aviao";
            var ex = Assert.Throws<RequestValidationException>(() => GestaoService.BuildMotoboyPerfilCommand(request));
            Assert.True(ex.Errors.ContainsKey("tipoVeiculo"));
        }

        [Fact]
        public void BuildCommand_RejectsUnknownPixType()
        {
            var request = Valid();
            request.PixTipo = "bitcoin";
            var ex = Assert.Throws<RequestValidationException>(() => GestaoService.BuildMotoboyPerfilCommand(request));
            Assert.True(ex.Errors.ContainsKey("pixTipo"));
        }

        [Fact]
        public void BuildCommand_RejectsUnknownStatusCadastro()
        {
            var request = Valid();
            request.StatusCadastro = "suspenso";
            var ex = Assert.Throws<RequestValidationException>(() => GestaoService.BuildMotoboyPerfilCommand(request));
            Assert.True(ex.Errors.ContainsKey("statusCadastro"));
        }

        [Fact]
        public void BuildCommand_EmptyOptionalFieldsBecomeNull()
        {
            var request = new AtualizarMotoboyPerfilRequest { Cep = "   ", PlacaMoto = "" };
            var command = GestaoService.BuildMotoboyPerfilCommand(request);

            Assert.Null(command.Cep);
            Assert.Null(command.PlacaMoto);
            Assert.Equal("ativo", command.StatusCadastro); // default quando nao informado
        }

        [Fact]
        public void BuildCommand_UppercasesPlacaAndUfAndCnhCategoria()
        {
            var request = Valid();
            request.PlacaMoto = "abc1d23";
            request.Uf = "mg";
            request.CnhCategoria = "ab";
            var command = GestaoService.BuildMotoboyPerfilCommand(request);

            Assert.Equal("ABC1D23", command.PlacaMoto);
            Assert.Equal("MG", command.Uf);
            Assert.Equal("AB", command.CnhCategoria);
        }
    }
}
