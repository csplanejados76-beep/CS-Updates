# CS USB Display v0.2.0

Transforma um tablet Android em segunda tela estendida do Windows usando somente cabo USB.

## Recursos da v0.2.0

- transporte de dados via USB usando ADB reverse
- segunda tela estendida real no Windows usando Virtual Display Driver
- selecao automatica da tela nao primaria
- video da segunda tela enviado ao tablet
- audio do Windows enviado ao tablet
- microfone do tablet enviado de volta ao Windows
- microfone virtual no Windows atraves do VB-CABLE
- supressao de ruido e cancelamento de eco do Android quando disponiveis
- PowerShell para preparar dependencias, colocar o Windows em modo Estender e iniciar a conexao
- porta TCP local escolhida automaticamente para evitar conflitos

## Requisitos

1. Windows 10/11 x64
2. Android 8.0 ou superior
3. Opcoes do desenvolvedor e Depuracao USB ativadas
4. Cabo USB com dados
5. Virtual Display Driver para criar o segundo monitor
6. VB-CABLE para expor o microfone do tablet como dispositivo de gravacao do Windows

## Instalacao rapida

1. Instale o APK CS-USB-Display no tablet.
2. Extraia todo o ZIP do Windows.
3. Conecte o tablet por USB e autorize a chave RSA da depuracao USB.
4. Clique com o botao direito em Start-CSUSBDisplay.ps1 e execute com PowerShell.
5. Na primeira execucao, o script pode oferecer a instalacao do Virtual Display Driver e do VB-CABLE.
6. Se o VB-CABLE for instalado, reinicie o Windows e execute o script novamente.
7. O PowerShell ativa DisplaySwitch /extend e inicia o host automaticamente.

## Microfone

O tablet envia audio mono PCM 48 kHz pelo mesmo cabo USB.

O host escreve o sinal em CABLE Input (VB-Audio Virtual Cable). Nos programas do Windows, selecione CABLE Output (VB-Audio Virtual Cable) como microfone.

## Audio do PC

O host captura o mix de reproducao padrao do Windows por WASAPI loopback, converte para PCM 16-bit estereo e envia ao tablet pelo USB.

## Tela estendida

O host lista os monitores que o Windows reconhece e prioriza automaticamente uma tela nao primaria. A tela tambem pode ser escolhida manualmente na interface.

O driver virtual e um componente separado do CS USB Display. O PowerShell usa o pacote publicado no winget pelo projeto Virtual Display Driver.

## Seguranca de instalacao

O script nao desativa Secure Boot nem habilita Test Signing.

O VB-CABLE e baixado do dominio oficial da VB-Audio e o instalador e aceito somente quando o Windows informa uma assinatura Authenticode valida.

## Estrutura do protocolo USB

PC para tablet:
- tipo 1: quadro JPEG
- tipo 2: audio PCM16 estereo, com sample rate no inicio do payload

Tablet para PC:
- tipo 3: microfone PCM16 mono 48 kHz

Cada pacote usa 1 byte de tipo, 4 bytes big-endian de comprimento e o payload.

## Observacao

Audio no Windows nao pertence fisicamente a um monitor. A v0.2.0 envia ao tablet o dispositivo de reproducao padrao do Windows. O video vem especificamente da segunda tela selecionada.
