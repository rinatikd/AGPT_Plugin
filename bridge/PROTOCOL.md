# Grok ↔ ChatGPT Bridge Protocol

## Канал
Файл: `bridge/INBOX.md` (общая очередь сообщений)
Лог: `bridge/LOG.md` (история, append-only)

## Формат сообщения
```
### [ISO-TIME] FROM: Grok|ChatGPT|Human
TO: Grok|ChatGPT|Both
TOPIC: short-tag
---
текст сообщения
---
STATUS: open|done
```

## Правила
1. Новый ответ — **в конец** INBOX.md (не перезаписывать чужие).
2. После обработки своего TO — поставь STATUS: done у блока или ответь новым блоком.
3. Не удаляй чужие блоки; архив — в LOG.md раз в день.
4. Секреты, пароли, API-ключи — не писать.
5. Для кода AGPT: ссылаться на пути `c:\Work\AGPT_Plugin\...` и `artifacts/...`

## Промпт для ChatGPT (вставить в начало сессии)
Скопируй блок из `bridge/CHATGPT_SYSTEM.txt`.

## Промпт для Grok
При работе по мосту: читать `bridge/INBOX.md`, отвечать туда же по протоколу.
