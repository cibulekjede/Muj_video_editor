# Můj video editor

Vlastní open source video editor ve stylu CapCut, napsaný v C# (.NET 10, WPF).

## Plánované funkce

- [x] Import videí, obrázků a zvuku (tlačítkem i přetažením)
- [x] Náhled videa (přehrát/pozastavit mezerníkem, posuvník)
- [x] Časová osa se stopou Video a Zvuk (přetažení z knihovny, dvojklik nebo tlačítko +)
- [x] Přehrávání celého sestřihu
- [x] Zkracování, dělení (Ctrl+B), přesouvání a mazání klipů, Zpět/Znovu (Ctrl+Z/Ctrl+Y)
- [ ] Text a titulky
- [ ] Přechody a efekty
- [ ] Export videa (pomocí FFmpeg)

## Požadavky

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Spuštění

```bash
dotnet run --project src/MujVideoEditor
```

## Licence

[MIT](LICENSE)
