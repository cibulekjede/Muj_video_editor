# Můj video editor

Vlastní open source video editor ve stylu CapCut, napsaný v C# (.NET 10, WPF).

## Plánované funkce

- [x] Import videí, obrázků a zvuku (tlačítkem i přetažením)
- [x] Náhled videa (přehrát/pozastavit mezerníkem, posuvník)
- [x] Časová osa se stopou Video a Zvuk (přetažení z knihovny, dvojklik nebo tlačítko +)
- [x] Přehrávání celého sestřihu
- [x] Zkracování, dělení (Ctrl+B), přesouvání a mazání klipů, Zpět/Znovu (Ctrl+Z/Ctrl+Y)
- [x] Ukládání a otevírání projektu (.mvproj), upozornění na neuložené změny
- [x] Text a titulky (předlohy, písmo, velikost, barva, stín, pozadí, přetažení v náhledu, export)
- [x] Poměr stran 16:9, 9:16 (TikTok/Reels), 1:1, 4:3, 3:4
- [x] Hlasitost a rychlost klipů (0,25× až 4×)
- [x] Přechody: prolnutí přes černou (roztmívání a zatmívání)
- [x] Plynulé přehrávání bez probliknutí na střihu, přichytávání na časové ose
- [x] Celá obrazovka (F), filtry knihovny a oblíbená média
- [ ] Filtry, efekty a nálepky
- [x] Export do MP4 (pomocí FFmpeg): 720p až 4K, 24–60 fps, volba kvality

## Požadavky

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [FFmpeg](https://ffmpeg.org/) pro export videa, nejsnáz přes `winget install Gyan.FFmpeg`

## Spuštění

```bash
dotnet run --project src/MujVideoEditor
```

## Licence

[MIT](LICENSE)
