# Můj video editor

Vlastní open source video editor ve stylu CapCut, napsaný v C# (.NET 10, WPF).

## Plánované funkce

- [x] Import videí, obrázků a zvuku (tlačítkem i přetažením)
- [x] Náhled videa (přehrát/pozastavit mezerníkem, posuvník)
- [x] Časová osa se stopou Video a Zvuk (přetažení z knihovny, dvojklik nebo tlačítko +)
- [x] Přehrávání celého sestřihu
- [x] Zkracování, dělení (Ctrl+B), přesouvání a mazání klipů, Zpět/Znovu (Ctrl+Z/Ctrl+Y)
- [x] Ukládání a otevírání projektu (.mvproj), upozornění na neuložené změny
- [ ] Text a titulky
- [ ] Přechody a efekty
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
