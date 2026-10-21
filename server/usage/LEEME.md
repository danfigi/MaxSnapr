# Recuento de uso de MaxSnapr

Un Worker de Cloudflare con una base de datos D1. Recibe el recuento diario y anónimo que envía la aplicación y muestra un panel privado con:

- usuarios activos por día, por semana y por mes;
- instalaciones nuevas;
- versiones en uso;
- funciones usadas;
- Windows, países, idiomas y tipo de instalación (instalada o portable).

## Qué se guarda

Solo sumas por día. Cada envío añade 1 a los totales de su combinación de versión, Windows, país, idioma y tipo de instalación, y suma los recuentos de funciones de ese día.

No hay ninguna fila por usuario, ni identificadores ni direcciones IP. Los registros del Worker están desactivados (`observability`, `logpush`). El país lo deduce Cloudflare y se guarda solo el código de dos letras.

## Puesta en marcha (una sola vez)

```bash
npx wrangler login
npx wrangler d1 create maxsnapr-usage
```

`d1 create` devuelve un `database_id`: cópialo en `wrangler.toml`. Después:

```bash
npx wrangler d1 execute maxsnapr-usage --remote --file schema.sql
npx wrangler secret put DASHBOARD_KEY
npx wrangler deploy
```

`secret put DASHBOARD_KEY` te pide la clave del panel: una frase larga que solo sepas tú.

## Ver el panel

Abre `https://maxsnapr-usage.<tu-subdominio>.workers.dev/?key=TU_CLAVE` una vez en tu navegador. La clave pasa a una cookie y desaparece de la barra de direcciones; a partir de ahí basta con la dirección sin `?key=`.

La dirección del Worker debe coincidir con `Usage.Endpoint` en `src/MaxSnapr/Services/Usage.cs`.

## Probar en local

```bash
npx wrangler d1 execute maxsnapr-usage --local --file schema.sql
echo DASHBOARD_KEY=prueba > .dev.vars
npx wrangler dev --local
```

Abre http://127.0.0.1:8787/?key=prueba.
