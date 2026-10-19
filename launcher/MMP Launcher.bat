@echo off
setlocal EnableExtensions

:: Windows Build Tespiti (22000 ve üzeri = Win11)
for /f "tokens=4-5 delims=[.] " %%i in ('ver') do set BUILD=%%i
if %BUILD% GEQ 22000 (
    goto WIN11_FLOW
) else (
    goto WIN10_FLOW
)

:WIN11_FLOW
if /i "%~1"=="/run" goto run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher.ps1"
exit /b

:WIN10_FLOW
if /i "%~1"=="/run" goto run_win10

chcp 65001 >nul
cls
echo          ,''',
echo        .' ., .',                                  ../'''',
echo       .'. %%, %.',                         .,/' .,%    :
echo      .'.% %%%,`%%%'.     .....,,,,,,,.....   .,%%% .,%%'. .'
echo      : %%% %%%%%%',:%%>>%>' .,>>%>>%>%>>%%>,.   `%%%',% :
echo      : %%%%%%%'.,>>>%'   .,%>%>%'.,>%>%' . `%>>>,. `%%%:'
echo      ` %%%%'.,>>%'  .,%>>%>%' .,%>%>%' .>>%,. `%%>>,. `%
echo       `%'.,>>>%'.,%%%%%%%' .,%%>%%>%' >>%%>%>>%.`%% %% `,
echo      ,`%% %%>>>%%%>>%%>%%>>%>>%>%%%  %%>%%>%%>>%>%%%' % %,
echo    ,%>%'.>>%>%'%>>%%>%%%%>%'              `%>%>>%%.`%>>%.
echo   ,%%>' .>%>%'.%>%>>%%%>>%' ,%%>>%%>%>>%>>%>%%,.`%>%%>%%. `%>%.
echo  ` ,%' .>%%%'.%>%>>%' .,%%%%%%%%'        `%%%%%%.`%%>%% .%%>
echo  .%>% .%%>' :%>>%%'.,%%%%%%%%%'.%%%%%' `%%%%.`%%%%%.%%%%> %%>%.
echo  ,%>%' >>%%  >%' `%%%%'     `%%%%%%%'.,>,. `%%%%'     `%%%>>%%>%
echo  .%%>%' .%%>'  %>>%, %% oO ~ Oo %%%>>'.>>>>>>. `% oO ~ Oo'.%%%'%>%,
echo  %>'%> .%>%>%  %%>%%%'   `OoooO'.%%>>'.>>>%>>%>>.`%`OoooO'.%%>% '%>%
echo  %',%' %>%>%'  %>%>% .%,>,>,   `>'.>>%>%%>>>%>.`%,>,>' %```% .>%>,
echo   ` %>% `%>>%%. `%%% %' >%%%%%%>,  ' >>%>>%%%>%>>> >>%%' ,%%>%'.%%>>%.
echo    .%%'  %%%%>%.   `>%%. %>%%>>>%.>> >>>%>%%%%>%>>.>>>'.>%>%>' %>>%>%%
echo    `.%%  `%>>%%>    %%%<>  %>>>%%%>>'.>%>>>>%%%>>%>>.>',%>>%'   ,>%'>% '
echo     %>'  %%%%%%    `%%'  %%%%% >' >>>>%>>%%>>%>>%> %%>%>' .%>%% .%%
echo    %>%>, %>%%>>%%,  %>%>% `%%  %>>   >>>%>>>%%>>>>%>>  %%>>,%>%%'.%>%,
echo   %>%>%%, `%>%%>%>%, %>%%> ,%>%>>>.>>`.,.  ``    ..'>.%. % %>%>%'.%>%%;
echo   %'`%%>%  %%>%%  %>% %'.>%>>%>%>>;'' ..,,%>%>%%/::%>%%>>%%,,.``% .%>%%
echo   ` .%>%'  >%%% %>%%'.>%>%;''.,>>%%>%%::.  ..'.,%>>%>%>,`%  %'.>%%' '
echo     %>%>%% `%>  >%%'.%%>%>>%>%>>>%>%>>%,,::,%>>%%>>%%,,.``% .%>%%
echo     `    `%>%>>%%' %>%%>>%>>%>%>%>%%>%/'       `%>%%>%>>%%% ' .%'
echo          %'  `%>% `%>%%;'' .,>>%>%/',;;;;;,;;;;,`%>%>%,`%'   '
echo          `    `   ` %>%%%>%%>%%;/ @a;;;;;;;;;;;a@   >%>%%'
echo                    `/////////';, `@a@@a@@a@@aa@',;`//'
echo                       `//////.;;,,............,,;;//'
echo                           `////;;;;;;;;;;;;;;;/'
echo                              `/////////////////'
echo.

if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" (
    start "" "%~dp0MMP.exe"
    exit /b 0
)
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0launcher.ps1" -Run
exit /b 0

:run
if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" (
  start "" "%~dp0MMP.exe"
  exit /b 0
)
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0launcher.ps1" -Run
exit /b 0

:run_win10
if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" (
  start "" "%~dp0MMP.exe"
  exit /b 0
)
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0launcher.ps1" -Run
exit /b 0
