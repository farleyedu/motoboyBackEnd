# Endereços do cliente — 10/10/2026

O cardápio web e o pedido do atendente reaproveitam `clientes`, `cliente_enderecos`, as conversas do WhatsApp e o núcleo existente de pedidos. A interface fica em `zippy-admin`; este ajuste não cria um novo aplicativo de cliente no Expo.

## Fluxo

- Cliente com sessão válida: consulta diretamente os endereços da sua loja, sem consultar a janela do WhatsApp novamente.
- Sem sessão: informa WhatsApp com DDD. Apenas uma **mensagem recebida nos últimos 60 minutos** libera o acesso; a janela de envio de 24h, mensagens de saída e datas futuras não autenticam.
- Sem contato recente: nome/endereço permanecem ocultos, com botão para abrir o WhatsApp da loja. A tela verifica novamente a cada 6 segundos e oferece verificação manual. Abrir o link não libera a etapa: a mensagem precisa chegar ao backend pelo fluxo existente.
- Depois da validação: a tela mostra “WhatsApp autenticado” e salva uma sessão própria de cliente por loja, com validade de 30 dias. A credencial aleatória só fica em hash no banco; expiração, cliente inativo, outra loja ou telefone incompatível são recusados. “Trocar número / sair” remove a credencial local e revoga a sessão no servidor.
- `Meus endereços` abre o gerenciamento mesmo com a sacola vazia. No checkout, o principal é sugerido, e os demais podem ser escolhidos.
- Cliente e atendente podem adicionar, editar, nomear (Casa/Trabalho), informar complemento/referência, excluir e escolher o principal. A exclusão pede confirmação na interface e é lógica no banco. O primeiro endereço é principal; excluí-lo escolhe um dos restantes.
- O cadastro antigo e endereços de pedidos anteriores, incluindo rascunhos do atendente e pré-pedidos web confirmados, são importados uma única vez. Endereços editados/excluídos não reaparecem do histórico. Coordenadas legadas com vírgula são reconhecidas; endereços sem ponto precisam ser localizados antes do pedido.
- O endereço informado em cada novo pedido é salvo para reutilização. O núcleo salva também o dos pedidos/rascunhos do painel. Pedidos antigos conservam o próprio endereço. O principal continua sincronizado com os campos antigos do cadastro para os fluxos existentes.
- O formulário antigo de cadastro também salva/escolhe o principal ao alterar seu endereço, conservando os demais endereços. Essa sincronização participa da mesma transação do cadastro.

## Contratos e implantação

Migração nova: `Migrations/Delivery/20261010_01_cliente_sessoes.sql`, com rollback correspondente. Reutiliza a migração existente `20261005_01_cliente_enderecos.sql`. O importador usa a marca `clientes.endereco_historico_importado`; a migração não modifica endereços gravados nos pedidos.

Sessão pública: `POST /api/cardapio/web/cliente/autenticar`, `GET/DELETE .../sessao`. CRUD: `POST .../enderecos`, `PUT/DELETE .../enderecos/{id}`. Consultas usam `estabelecimentoId`; operações autenticadas usam `X-Cliente-Sessao`. O pedido público agora exige `sessaoCliente` e aceita `enderecoPrincipal`/`apelidoEndereco`.

Atendente: `GET/POST /api/v2/clientes/{clienteId}/enderecos`, `PUT/DELETE .../enderecos/{id}`, com permissões Delivery existentes e isolamento da loja. Pedidos do núcleo aceitam `enderecoPrincipal`/`apelidoEndereco`.

As mudanças estão locais. Publicar backend/migração e frontend em conjunto, pois o contrato público passa a exigir sessão. Nenhum deploy ou mensagem real de WhatsApp foi feito nesta validação.

## Validação

Resultado final: **112 testes backend passaram, sem ignorados**, incluindo os três testes de PostgreSQL do fluxo; **98 testes frontend passaram**, TypeScript sem erros e **9 cenários no Chromium móvel passaram**. A revisão visual das capturas conferiu a identificação correta do telefone e a lista no gerenciamento; a jornada completa do painel/WhatsApp real não foi homologada nesta execução.

Testes de autenticação, criação pública, confirmação, núcleo e PostgreSQL local em `Tests/Unit/ClienteAcessoTests.cs`, testes existentes de cardápio/pedido e `Tests/Integration/ClienteEnderecosDatabaseTests.cs`. Cobrem a janela de 1h, ausência de entrada, sessão lembrada, loja/telefone, principal concorrente, duplicação, importação, edição, exclusão e expiração/revogação. PostgreSQL usa somente loopback, com schemas temporários isolados; resultados locais em `obj/cliente-address-validation/results/cliente-enderecos.trx`.

Roteiro do frontend: `zippy-admin/docs/validacao-enderecos/browser.cjs`. Executa a interface real com API controlada em Chromium com largura móvel: bloqueio, liberação após mensagem, principal, edição, adição, exclusão, sessão após reload, pedido com endereço salvo e troca de número. Capturas/resultados em `obj/cliente-address-validation/captures`. Essa execução não simula como evidência um WhatsApp real; a integração com o provedor continua usando os webhooks existentes.
