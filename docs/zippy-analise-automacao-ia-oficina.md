# Zippy - Analise da Automacao com IA para Oficina

## 1. Objetivo desta analise

Esta analise mapeia como a automacao com IA funciona hoje no backend e como o projeto deveria evoluir para suportar um novo fluxo de atendimento de WhatsApp com IA para o segmento de oficina.

O foco aqui nao e so o fluxo da conversa. O objetivo e cobrir:

- novo tipo de estabelecimento
- novo modulo para guardar e operar os contatos que entram via WhatsApp
- padrao tecnico da automacao com IA
- comparacao com o que ja existe em garagem e nautica
- impacto em permissoes, JWT, gestao, banco e painel

---

## 2. Como a automacao com IA funciona hoje

Hoje a automacao do Zippy tem dois modos de funcionamento:

### 2.1 Fluxo generico com IA por prompt

Esse e o caminho usado para estabelecimentos comuns, principalmente os que trabalham com FAQ, reservas e atendimento geral.

O pipeline atual e:

1. WhatsApp webhook recebe a mensagem
2. `ConversationProcessor` registra a mensagem e monta contexto
3. `ContextInterceptorService` tenta interceptar fluxos que tem estado ativo
4. se nao houver interceptacao, a mensagem segue para `AssistantService`
5. a IA recebe:
   - prompt geral
   - prompt por modulo ativo
   - prompt especifico do estabelecimento
   - historico recente da conversa
6. a resposta volta como JSON estruturado
7. `IAResponseHandler` envia a resposta ao cliente pelo WhatsApp

### 2.2 Fluxo fechado por vertical

Garagem e nautica nao dependem do prompt generico para conduzir o atendimento principal.

Nesses dois casos, existe um padrao melhor definido:

- a conversa entra
- o `ContextInterceptorService` intercepta antes da IA livre
- um servico da vertical controla a etapa atual
- os dados vao sendo persistidos em uma tabela propria de lead
- o painel administrativo le essa tabela por controller e repository proprios

Ou seja:

**para fluxos estruturados de negocio, o projeto atual ja privilegia uma maquina de estados no backend, e nao um bot totalmente solto por prompt**

Essa conclusao e central para oficina.

---

## 3. O padrao tecnico real do projeto hoje

Analisando o codigo, o padrao atual da automacao e este:

### 3.1 A conversa e um objeto operacional separado do lead

O sistema separa bem:

- conversa WhatsApp
- contexto da conversa
- lead ou cadastro operacional da vertical

Isso e importante porque evita misturar:

- historico de chat
- estado do atendimento
- dados de negocio do cliente

Para oficina, isso deve ser mantido.

### 3.2 O contexto da conversa e persistido

O fluxo nao depende so de memoria temporaria.

Estados como:

- selecao de estabelecimento
- etapa atual do questionario
- escolha aguardando confirmacao
- fluxo pausado

ficam persistidos no contexto da conversa.

Para oficina, isso significa que o fluxo deve ser construido com:

- etapas claras
- contexto salvo
- retomada segura
- reset controlado

### 3.3 A IA generica hoje nao foi desenhada para operar leads complexos

As tools atuais da IA sao centradas em:

- confirmar reserva
- listar reservas
- atualizar reserva
- cancelar reserva
- escalar para humano

Isso significa que o motor generico de IA atual sabe operar reserva, mas nao sabe operar oficina.

Se oficina nascer apenas como prompt, a IA nao tera ferramentas nem contratos estruturados para:

- abrir atendimento tecnico
- registrar placa e veiculo
- classificar sintoma
- capturar urgencia
- encaminhar para recepcao/oficina
- alimentar um painel proprio

Conclusao:

**oficina nao deve nascer so como prompt em `ia_regras`**

Ela deve nascer como vertical com fluxo proprio, nos moldes de garagem e nautica.

---

## 4. O que garagem ensina para oficina

Garagem hoje e o melhor espelho para este projeto por 3 motivos:

1. ja usa fluxo estruturado via WhatsApp
2. ja persiste lead proprio por estabelecimento
3. ja tem painel e permissoes separados

### 4.1 O que ja existe em garagem

Garagem ja faz:

- cria ou reaproveita lead aberto por telefone + estabelecimento
- controla etapa atual
- salva dados parciais ao longo da conversa
- permite continuar um atendimento interrompido
- expande para painel interno
- trabalha com status de lead
- usa modulo de permissao separado do chat

### 4.2 Como as permissoes de garagem estao organizadas

Hoje a parte operacional esta dividida principalmente assim:

- `WhatsApp`
  - visualizar conversa
  - assumir conversa
  - devolver para robo
  - enviar mensagem
  - encerrar
  - reabrir
  - arquivar
  - configurar token/webhook

- `Leads`
  - visualizar
  - mudar_status
  - criar_simulacao
  - editar_simulacao
  - anexar_arquivo
  - remover_arquivo
  - excluir_simulacao

- `Estoque`
  - operar os veiculos

Para oficina, a leitura correta e:

- o chat continua pertencendo ao modulo `WhatsApp`
- o cadastro operacional dos contatos deve ficar em um modulo proprio

### 4.3 O que deve ser reaproveitado da garagem

O que oficina deveria copiar da garagem:

- fluxo dedicado por servico
- repositorio proprio da vertical
- tabela propria de contato
- controller de painel proprio
- status operacionais
- separacao entre chat e dados do atendimento
- validacao de acesso por estabelecimento ativo

---

## 5. O que nautica ensina para oficina

Nautica reforca que o padrao do projeto nao e apenas "perguntas em sequencia".

Ela mostra um segundo aprendizado:

- o lead pode ter etapas especializadas
- o estado pode ser salvo no proprio lead
- o fluxo pode qualificar, desqualificar ou concluir

Para oficina, isso e util porque o atendimento tambem precisa classificar o contato:

- so quer preco
- quer agendar avaliacao
- esta com o carro parado
- quer revisao preventiva
- quer orcamento de servico especifico

Ou seja:

**oficina tambem precisa de triagem, nao apenas cadastro**

---

## 6. Recomendacao de arquitetura para oficina

## 6.1 Recomendacao principal

Criar oficina como uma nova vertical de atendimento, seguindo o padrao de garagem e nautica.

Isso significa:

- novo tipo de estabelecimento: `oficina`
- novo fluxo dedicado de WhatsApp
- nova persistencia dos contatos
- novo painel para operar esses contatos
- novas permissoes por modulo

### 6.2 O que nao recomendo

Nao recomendo implementar oficina assim:

- apenas adicionando um prompt em `ia_regras`
- apenas reaproveitando reservas
- apenas salvando tudo no historico da conversa

Esse caminho fica fragil porque:

- nao cria estrutura operacional
- nao cria painel consistente
- nao cria governanca de permissao
- nao padroniza a coleta de informacoes

---

## 7. Novo tipo de estabelecimento

## 7.1 Tipo recomendado

Criar um novo tipo de estabelecimento com:

- slug: `oficina`
- nome: `Oficina`

Esse tipo precisa existir no catalogo do banco em `tipo_estabelecimento`.

### 7.2 Impactos do novo tipo

Criar o tipo novo impacta:

- cadastro de estabelecimento
- selecao de estabelecimento
- JWT com tipo do estabelecimento
- regras de papeis permitidos por tipo
- logica de automacao que hoje decide por slug

### 7.3 Onde isso bate no codigo

Principalmente nestes pontos:

- `Repository/GestaoRepository.cs`
- `Automation/Infra/SqlEstabelecimentoRepository.cs`
- `Security/RoleCatalog.cs`
- `Automation/Services/ConversationProcessor.cs`
- `Automation/Services/ContextInterceptorService.cs`

### 7.4 Observacao importante

Hoje o backend usa o tipo de estabelecimento de duas formas:

- nome em alguns fluxos de gestao/JWT
- slug em alguns fluxos de automacao

Para oficina, slug e nome precisam estar coerentes desde o inicio.

---

## 8. Novo modulo para guardar os contatos

## 8.1 Requisito do negocio

Voce deixou claro que quer:

- um novo modulo
- esse modulo deve guardar as informacoes dos clientes que entram em contato

Pelo desenho atual, esse requisito pode ser resolvido de duas formas:

### Opcao A: reaproveitar `Leads`

Vantagens:

- menor custo tecnico
- segue o padrao de garagem e nautica
- menos mudanca em enum, mapeador e permissoes

Desvantagem:

- nao cumpre literalmente a ideia de um modulo novo
- semanticamente continua parecendo CRM comercial, e nao atendimento tecnico

### Opcao B: criar modulo novo

Vantagens:

- cumpre o requisito literal
- separa oficina de garagem/nautica
- deixa o painel mais claro para a operacao

Desvantagens:

- aumenta trabalho em banco, JWT, mapeadores, permissoes e front

## 8.2 Minha recomendacao

Se a decisao e realmente criar modulo novo, o nome mais consistente e:

**`Atendimentos`**

Nao recomendo chamar o modulo de `Oficina`, porque `Oficina` e a vertical. O modulo deve representar a capacidade de produto.

Esse modulo seria o responsavel por:

- listar contatos entrantes
- abrir detalhe do atendimento
- ver dados do veiculo
- ver resumo do problema
- mudar status operacional
- anexar arquivos
- registrar observacoes internas

Enquanto isso:

- `WhatsApp` continua sendo o modulo de conversa
- `Atendimentos` vira o modulo de operacao dos contatos da oficina

## 8.3 Nome tecnico sugerido

- enum DB: `ATENDIMENTOS`
- nome no painel/JWT: `Atendimentos`

---

## 9. Modelo de dados recomendado para oficina

## 9.1 Tabela principal

Criar uma tabela propria, seguindo o padrao `cliente_garagem` e `cliente_nautica`.

Nome sugerido:

- `cliente_oficina`

ou, se quiser um nome mais orientado ao processo:

- `oficina_atendimento`

## 9.2 Campos recomendados

Campos minimos para v1:

- `id`
- `id_estabelecimento`
- `id_conversa`
- `id_cliente`
- `telefone_e164`
- `nome_cliente`
- `placa`
- `veiculo_marca`
- `veiculo_modelo`
- `veiculo_ano`
- `quilometragem`
- `motivo_contato`
- `categoria_servico`
- `descricao_problema`
- `nivel_urgencia`
- `carro_anda`
- `precisa_guincho`
- `deseja_agendamento`
- `melhor_periodo`
- `status`
- `etapa_atual`
- `ultima_pergunta`
- `resumo_ia`
- `via_numero_central`
- `data_conclusao`
- `data_criacao`
- `data_atualizacao`

## 9.3 Campos opcionais que fazem sentido cedo

- `origem_contato`
- `bairro_cliente`
- `aceite_orcamento_previo`
- `tipo_combustivel`
- `cpf`
- `observacoes_internas`
- `tags_json`

## 9.4 Status sugeridos

Status recomendados para oficina:

- `novo`
- `em_triagem`
- `aguardando_cliente`
- `aguardando_agendamento`
- `encaminhado`
- `concluido`
- `cancelado`

Se quiser simplificar v1:

- `em_andamento`
- `aguardando_cliente`
- `encaminhado`
- `concluido`
- `cancelado`

---

## 10. Fluxo de atendimento recomendado para oficina

## 10.1 Regra principal

O fluxo de oficina deve ser:

**estruturado no backend + IA para interpretar texto livre**

Nao o contrario.

## 10.2 Padrao ideal

### Camada 1: maquina de estados

O backend controla:

- em que etapa o cliente esta
- quais campos faltam
- qual e a proxima pergunta
- quando encerrar
- quando encaminhar para humano

### Camada 2: IA

A IA ajuda em:

- interpretar respostas livres
- classificar intencao inicial
- resumir problema em linguagem interna
- sugerir prioridade
- padronizar sintomas

### Camada 3: operacao humana

Humano entra quando:

- cliente pede preco exato
- cliente manda audio/imagem complexa
- ha risco mecanico ou seguranca
- o carro nao anda
- o cliente quer negociar
- a IA nao consegue extrair os dados com confianca

## 10.3 Fluxo sugerido

Fluxo v1 sugerido:

1. saudacao
2. identificar nome
3. entender motivo do contato
4. identificar veiculo
5. perguntar se o carro anda ou se precisa de guincho
6. coletar urgencia
7. coletar melhor periodo para retorno/agendamento
8. gerar resumo do atendimento
9. confirmar dados
10. abrir ou atualizar atendimento
11. encaminhar para recepcao/oficina

## 10.4 Intencoes iniciais recomendadas

Logo no inicio, a IA deve classificar ou o fluxo deve oferecer botoes como:

- Revisao
- Orcamento
- Problema mecanico
- Freio/suspensao
- Eletrica/bateria
- Motor/aquecimento
- Pneus/alinhamento
- Guincho
- Agendamento

## 10.5 Campos obrigatorios antes do handover

Antes de encaminhar, oficina deveria tentar sair com pelo menos:

- nome
- telefone
- veiculo ou placa
- problema principal
- urgencia

Se nao conseguir coletar tudo, ainda assim o contato pode ser criado com status:

- `novo`
- ou `dados_incompletos`

---

## 11. Como a IA deve ser usada em oficina

## 11.1 O papel da IA

A IA nao deve ser a dona da logica de negocio.

Ela deve ser usada para:

- entender linguagem natural
- extrair dados de texto solto
- resumir conversa
- classificar severidade
- identificar intencao

## 11.2 O que deve continuar fora da IA

Deve continuar no backend:

- transicao de etapa
- persistencia dos campos
- regras de obrigatoriedade
- abertura e fechamento de atendimento
- regras de permissao
- roteamento para humano

## 11.3 Padrao recomendado para respostas

Padrao recomendado para oficina:

- perguntas curtas
- uma coleta por vez
- confirmacao final antes de concluir triagem
- botoes nas opcoes simples
- texto livre quando o cliente precisar explicar defeito

## 11.4 Resumo interno gerado por IA

Um campo muito util para oficina e `resumo_ia`, por exemplo:

- "Cliente com HB20 2018, relata falha ao ligar, bateria descarrega, urgencia alta, carro parado, deseja retorno ainda hoje."

Isso aumenta muito a eficiencia da equipe interna.

---

## 12. Permissoes recomendadas para oficina

## 12.1 Separacao por modulo

A separacao recomendada e:

- `WhatsApp`
  - opera a conversa
- `Atendimentos`
  - opera o cadastro do contato/oficina

## 12.2 Acoes recomendadas para `Atendimentos`

Permissoes minimas sugeridas:

- `visualizar`
- `visualizar_detalhe`
- `editar`
- `mudar_status`
- `anexar_arquivo`
- `adicionar_observacao`

Permissoes opcionais para v2:

- `registrar_orcamento`
- `agendar`
- `converter_os`
- `exportar`

## 12.3 Papeis recomendados para oficina

Se quiser minimizar impacto no core, use apenas papeis ja existentes:

- `gerente_estabelecimento`
- `atendente`
- `funcionario`

Leitura pratica:

- `atendente`: recepcao e triagem
- `funcionario`: apoio interno
- `gerente_estabelecimento`: controle e operacao

## 12.4 Se quiser papel novo

Se quiser um papel novo como `consultor` ou `mecanico`, isso exige mais mudanca:

- `Security/RoleCatalog.cs`
- permissoes padrao por tipo
- tela de usuarios
- validacao de papeis permitidos por tipo de estabelecimento

Para v1, eu nao recomendo abrir isso se o objetivo principal e colocar oficina no ar rapido.

## 12.5 Recomendacao final de permissao

Para v1:

- manter papeis existentes
- criar permissao nova por modulo
- deixar o atendimento tecnico controlado por `Atendimentos`

---

## 13. Como oficina entra no pipeline atual

## 13.1 Onde precisa mexer

Para oficina seguir o padrao certo, os impactos principais sao:

### Core de gestao

- cadastrar `oficina` em `tipo_estabelecimento`
- cadastrar `Atendimentos` em `modulos_disponiveis`
- incluir o novo modulo no enum `modulo_enum`
- incluir o modulo no `EstabelecimentoModuleMapper`
- incluir permissoes padrao em `permissoes_padrao`

### Seguranca e papeis

- liberar papeis validos para `oficina` em `RoleCatalog`

### Automacao

- criar `OficinaFlowService`
- criar `IOficinaLeadRepository`
- criar `SqlOficinaLeadRepository`
- criar modelo `OficinaLead`
- criar painel `OficinaAtendimentosController`
- criar repository de painel de oficina

### Orquestracao

- registrar tudo no `Program.cs`
- fazer `ContextInterceptorService` chamar o fluxo de oficina
- fazer `ConversationProcessor` suprimir prompt generico para oficina, se o fluxo for totalmente dedicado

## 13.2 Sobre suprimir ou nao a IA generica

Minha recomendacao:

- durante triagem ativa: interceptar e nao deixar cair na IA generica
- fora da triagem: pode responder FAQ generico ou seguir para humano

Em outras palavras:

**oficina deve usar fluxo dedicado como caminho principal**

---

## 14. Desenho tecnico recomendado para o servico de oficina

## 14.1 Novo servico

Criar:

- `OficinaFlowService`

Com responsabilidade de:

- identificar se o estabelecimento e oficina
- criar ou retomar atendimento aberto
- determinar etapa atual
- interpretar a resposta do cliente
- salvar progresso
- concluir triagem
- devolver resposta com ou sem botoes

## 14.2 Novas constantes de estado

Exemplo:

- `oficina_questionario`
- `oficina_atendimento_concluido`
- `oficina_aguardando_confirmacao`

## 14.3 Novo painel

Criar rota dedicada, por exemplo:

- `oficina/atendimentos`

Ela deve permitir:

- listagem
- detalhe
- mudanca de status
- anotacoes
- anexos

---

## 15. Recomendacao de MVP

## 15.1 MVP tecnico

Se o objetivo e colocar no ar rapido sem aumentar demais o risco, o MVP ideal e:

1. criar tipo `oficina`
2. criar modulo `Atendimentos`
3. criar tabela `cliente_oficina`
4. criar `OficinaFlowService`
5. criar painel basico de listagem e detalhe
6. manter papeis existentes
7. manter `WhatsApp` separado de `Atendimentos`

## 15.2 O que deixar para fase 2

- orcamento detalhado
- OS completa
- integracao com agenda/oficina
- upload de fotos do veiculo com classificacao automatica
- sugestao de diagnostico por IA
- SLA e fila interna

---

## 16. Riscos e cuidados

## 16.1 Risco de fazer oficina so por prompt

Se fizer apenas por prompt:

- o atendimento vira texto solto
- a operacao nao ganha painel consistente
- os dados ficam incompletos
- o handover perde contexto

## 16.2 Risco de criar modulo novo sem alinhar banco

Como o projeto usa:

- `modulo_enum`
- `modulos_disponiveis`
- `permissoes_padrao`
- `modulos_ativos` por estabelecimento

o novo modulo exige alinhamento real no banco.

Hoje esse versionamento SQL nao esta no repositorio, entao essa parte precisa ser planejada com cuidado.

## 16.3 Risco de abrir papeis demais

Se criar papel novo junto com vertical nova e modulo novo, o escopo cresce muito.

Por isso, para v1:

- tipo novo: sim
- modulo novo: sim
- papel novo: so se for realmente necessario

---

## 17. Conclusao executiva

A automacao com IA do Zippy hoje ja mostra um padrao muito claro:

- atendimento simples pode cair em IA por prompt
- atendimento de negocio com coleta estruturada vira fluxo dedicado

Garagem e nautica provam isso.

Por isso, oficina deve entrar como:

**nova vertical operacional, com fluxo proprio, persistencia propria e painel proprio**

Do ponto de vista de arquitetura, a recomendacao mais consistente e:

1. criar o tipo de estabelecimento `oficina`
2. criar o modulo `Atendimentos`
3. manter `WhatsApp` como modulo de conversa
4. criar a tabela `cliente_oficina`
5. criar `OficinaFlowService` nos moldes de garagem/nautica
6. criar controller/repository de painel para operar esses contatos
7. usar IA como camada de interpretacao e resumo, nao como unica regra de negocio

Em resumo:

**para oficina, o melhor desenho nao e um bot livre. E um fluxo operacional com IA assistindo a triagem.**
