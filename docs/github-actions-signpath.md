# GitHub Actions и SignPath

## Что добавлено
- `LICENSE` с лицензией MIT.
- `.github/workflows/ci.yml` для сборки и публикации артефактов.
- `.github/workflows/release-signpath.yml` для публикации клиента и отправки его в SignPath.

## Какие секреты нужны в GitHub
Добавь в `Settings -> Secrets and variables -> Actions`:

- `SIGNPATH_API_TOKEN`
- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY_SLUG`
- `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` — опционально, если используется отдельная конфигурация артефакта

## Что нужно настроить в SignPath
Для open source проекта обычно используется сертификат через программу SignPath Foundation.

Нужно:
1. Создать организацию или проект в SignPath.
2. Подключить GitHub repository как trusted build source.
3. Создать `project slug`.
4. Создать `signing policy slug`.
5. При необходимости создать `artifact configuration slug`.
6. Выпустить API token для GitHub Actions.

Документация:
- https://docs.signpath.io/trusted-build-systems/github
- https://about.signpath.io/open-source

## Важное про SmartScreen
Подпись кода уменьшает число предупреждений и повышает доверие к файлу, но не гарантирует мгновенное исчезновение SmartScreen.

На репутацию влияют:
- действительная подпись кода;
- стабильное имя издателя;
- одинаковое имя файла/продукта;
- регулярные скачивания без жалоб;
- публикация релизов с GitHub Releases или собственного домена.

## Практические рекомендации
- Распространяй только подписанный `ArmaLauncherClient.exe`.
- Не меняй часто `Product`, `Company` и имя exe.
- Публикуй релизы из GitHub Releases, а не случайными архивами.
- Добавь ссылку на исходный код, лицензию и хэши релиза в README.
- По возможности используй HTTPS-домен проекта для страницы загрузки.
