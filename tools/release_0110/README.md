# Проверка опубликованных бандлов

`verify_bundles.py` проверяет **скачанные** архивы, а не предполагает успех по зелёному workflow.
Вход: две папки `latest/` и `build-N/`, по пять `*.bundle.zip`; сохранённое наблюдение API
с результатами `build_workflow` / `check_workflow`, SHA тега `build-N`, SHA `main` до/после.
Скачивать следует из `https://github.com/zinchenko-denis/AutoCAD/releases/download/<tag>/<asset>`.

Зависимости инспектора: Python 3.12, `pyinstaller==6.22.3`, `dnfile==0.18.0`.
Они нужны только инструменту проверки и не добавляются в плагины.

Пример (параметры конкретной сборки передаются явно):

```sh
python tools/release_0110/verify_bundles.py --downloads /path/to/downloads --build 104 --sha ec5160983f61ce5668369d2513313391db4d6c5d --source-ref ec5160983f61ce5668369d2513313391db4d6c5d --publication-evidence /path/to/publication_evidence.json --out /path/to/receipt.json
```

Проверяются:

- полное байтовое совпадение каждой пары `latest` / `build-N`, CRC ZIP, отсутствие опасных/повторных имён и точный комплект файлов;
- номер/попытка/коммит/ветка и версии из `build-info.json`;
- все исходные файлы бандла (XML, значки, формы), `systems.json` / `mapping.yaml` через `git show` **собранного коммита**: бинарные файлы побайтово; для XML/JSON/YAML разрешается только проверенное LF/CRLF преобразование Windows checkout, с записью обоих SHA;
- корректные заголовки AMD64 PE, CLR metadata и обязательные новые типы DLL;
- entrypoint и требуемые модули внутри архива PyInstaller, включая новые `frame_catalog`, `frame_connections`, `frame_spatial`.

Содержимое EXE инспектируется без запуска, DLL не загружается. `PASS` означает проверку
публикации и комплектности; **не** Windows smoke, приёмку в AutoCAD или подтверждение
инженерной применимости. Эти границы сохранены отдельными `false` в JSON. При ошибке
инструмент возвращает ненулевой код и записывает `FAIL`, не оставляет фиктивный успех.

После проверки публикуется только JSON-квитанция, инструмент и инструкция. Скачанные
архивы/извлечённые DLL/EXE остаются вне исходного репозитория.
