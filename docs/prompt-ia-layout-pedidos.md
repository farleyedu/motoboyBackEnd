# Prompt para a IA de layout: quadro de Pedidos (iFood)

Você vai decidir o layout e o visual de três partes da tela de Pedidos do painel (`zippy-admin`, quadro com colunas Novos, Preparando, Prontos, Na rua, Entregues). Eu explico o que cada parte precisa fazer e por que. Como mostrar, onde colocar, cores, textos, animações e componentes é decisão sua. Siga o estilo visual que o painel já tem.

## Contexto que você precisa saber

Os pedidos do iFood não mudam de status na hora. Quando o operador avança um pedido (arrasta o card, ou usa o botão verde), o painel envia um comando ao iFood. O iFood aceita o comando, mas só confirma quando manda um evento de volta, o que leva cerca de 30 a 60 segundos. Só então o pedido muda de verdade.

Também há casos em que o comando é recusado ou fica travado. Exemplos: o pedido está em revisão, o prazo de confirmação acabou, o iFood recusou, ou já existe outra operação em andamento nesse pedido.

Hoje isso gera uma tela confusa:
- O painel diz "Pedido #N atualizado" antes de o iFood confirmar, e o card fica parado sem nenhum sinal.
- Quando dá errado, o operador não sabe por quê. A mensagem é genérica e some.
- Por isso o operador acha que o sistema é instável: às vezes muda, às vezes não.

## 1. Arrastar o card (movimento e estados)

O que precisa acontecer:
- Ao soltar o card numa coluna, ele fica na coluna de destino imediatamente, já com um estado visível de "atualizando".
- Se o iFood confirmar, o card continua lá e o estado de "atualizando" sai.
- Se falhar (recusa, bloqueio, tempo esgotado), o card volta para a coluna de origem e o motivo aparece de forma clara.
- Enquanto o card está "atualizando", ele não pode receber outro comando. Deve ficar claro que está esperando.
- Se o operador soltar o card numa coluna que não é a próxima etapa válida, o painel deve avisar, sem enviar nada.

Por quê: o operador precisa ver na hora que o sistema entendeu o que ele fez e, ao mesmo tempo, saber que o resultado ainda não é definitivo. Se falhar, o card não pode ficar num lugar que não é a verdade.

Estados a representar: normal, enviando, aguardando confirmação do iFood, confirmado, recusado/voltou, travado (precisa de ação do operador).

## 2. Mensagem de erro com motivo claro

O que precisa acontecer:
- Todo erro diz em português simples o que houve, por que, e o que o operador pode fazer.
- O motivo fica ligado ao card, para o operador entender qual pedido deu problema, e não só numa mensagem solta que desaparece.
- Os motivos mais comuns são: pedido em revisão, prazo de confirmação vencido, iFood recusou o comando, já existe uma operação em andamento, falta localização de entrega, sem permissão para operar o iFood, falha de conexão.

Por quê: hoje a mesma mensagem vale para tudo e o operador não sabe se deve esperar, tentar de novo ou chamar alguém.

## 3. Avisos (alertas) do iFood

Situação atual:
- Aparece um aviso flutuante no canto, parcialmente escondido pela coluna "Na rua", com texto cortado ("Pedidos em revisão que precisam…") e a contagem "3 pedidos precisam de conferência".
- Existe também o botão "1 aviso iFood" na barra de cima.
- Ao clicar em "Conferir", abre a busca geral de comandos (a paleta), que não tem nenhuma relação com o aviso. Isso não faz sentido.

O que precisa acontecer:
- O aviso mostra quais pedidos estão com problema e qual é o problema, sem cortar texto e sem cobrir o quadro.
- Clicar no aviso leva o operador direto ao pedido (ou à lista dos pedidos afetados), com o motivo e a ação possível para resolver.
- O operador consegue resolver ou dispensar o aviso, e os avisos não se acumulam nem se repetem.
- Deve existir diferença visível entre aviso que exige ação agora e aviso só informativo.

Por quê: o aviso existe para o operador agir, mas hoje ele só incomoda e leva a um lugar errado.

## Entrega esperada

Para cada uma das três partes: a decisão de layout (o que aparece, onde, como se comporta) com a justificativa, os estados representados e como o operador sai de cada um. Não precisa escrever código. Diga também se algum estado exige informação que o painel hoje não recebe do backend.
