#!/usr/bin/env node
// The home API's published contract, docs/api/openapi.json, and checks against it. No dependencies.
//
//   node scripts/api-contract.mjs schema --openapi <server.json> --xml <plugin.xml> --out <file> [--check]
//     Builds the contract from what Jellyfin serves at /api-docs/openapi.json for the plugin, with
//     descriptions from the plugin's XML documentation. --check compares it with <file> instead of
//     writing it, and fails on any difference.
//   node scripts/api-contract.mjs validate --schema <file> --operation <operationId> [--status 200] <body.json>
//     Checks a response body against the contract.

import { readFileSync, writeFileSync } from 'node:fs'
import { pathToFileURL } from 'node:url'

export const PREFIX = '/ExperienceManager/'

// The web client's own files: served by the plugin, but not API.
export const CLIENT_FILES = ['/ExperienceManager/home.js', '/ExperienceManager/home.css', '/ExperienceManager/admin-programming.js', '/ExperienceManager/admin-audiences.js', '/ExperienceManager/admin-layout.js', '/ExperienceManager/admin-rules.js']

// The admin page's own routes: administrators only, free to change, and not for client authors.
export const ADMIN_PREFIX = '/ExperienceManager/Admin/'

export const isPublicRoute = route => route.startsWith(PREFIX) && !CLIENT_FILES.includes(route) && !route.startsWith(ADMIN_PREFIX)

// Jellyfin's own item shape is referenced, not copied: it is Jellyfin's to define and changes with it.
const JELLYFIN_TYPES = {
    BaseItemDto: {
        type: 'object',
        description: "A Jellyfin item, exactly as Jellyfin's own item routes return it. Its schema is Jellyfin's `BaseItemDto`, in the server's own OpenAPI document at `/api-docs/openapi.json`.",
        additionalProperties: true
    }
}

// Named in the contract before they exist, so clients can plan for them.
const RESERVED = {
    '/ExperienceManager/v1/Me/Preferences': {
        get: {
            tags: ['Home API'],
            operationId: 'GetPreferences',
            summary: 'Reserved, not yet available: the signed-in user\'s own home screen preferences.',
            description: 'This route is named so clients can plan for it. Servers answer 404 until a version that serves it; `/ExperienceManager/Info` will list it in `Features` when it does.',
            'x-not-yet-available': true,
            responses: { 404: { description: 'Not yet available.' } }
        }
    }
}

const INFO = {
    title: 'Experience Manager home API',
    version: '1',
    description: 'The home screen the Experience Manager plugin serves for Jellyfin, for any client to draw: pages of sections, each section\'s items as Jellyfin items, and discovery. See docs/api/README.md for the integration guide.',
    license: { name: 'MIT', url: 'https://github.com/pathimmersive/jellyfin-plugin-experience-manager/blob/main/docs/api/LICENSE' }
}

// --- the plugin's XML documentation -----------------------------------------------------------

function text(xml) {
    return (xml || '')
        .replace(/<c>([\s\S]*?)<\/c>/g, '`$1`')
        .replace(/<see\s+cref="(?:[A-Z]:)?([^"]+)"\s*\/>/g, (m, name) => '`' + name.split('.').pop() + '`')
        .replace(/<(?:paramref|typeparamref)\s+name="([^"]+)"\s*\/>/g, '`$1`')
        .replace(/<[^>]+>/g, '')
        .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&')
        .replace(/\s+/g, ' ')
        .trim()
}

// Members by kind: methods by name, types by name, each with its summary, parameters and returns.
export function readDocs(xml) {
    const methods = new Map()
    const types = new Map()
    for (const m of xml.matchAll(/<member name="([MT]):([^"(]+)(?:\([^"]*\))?">([\s\S]*?)<\/member>/g)) {
        const [, kind, fullName, body] = m
        const params = {}
        for (const p of body.matchAll(/<param name="([^"]+)">([\s\S]*?)<\/param>/g)) params[p[1]] = text(p[2])
        const entry = {
            summary: text((body.match(/<summary>([\s\S]*?)<\/summary>/) || [])[1]),
            returns: text((body.match(/<returns>([\s\S]*?)<\/returns>/) || [])[1]),
            params
        }
        const name = fullName.split('.').pop()
        if (kind === 'M' && fullName.includes('.Controllers.')) methods.set(name, entry)
        if (kind === 'T') types.set(name, entry)
    }
    return { methods, types }
}

// --- building the contract ----------------------------------------------------------------------

function refsIn(value, found) {
    if (Array.isArray(value)) value.forEach(v => refsIn(v, found))
    else if (value && typeof value === 'object') {
        for (const [key, v] of Object.entries(value)) {
            if (key === '$ref' && typeof v === 'string') found.add(v.replace('#/components/schemas/', ''))
            else refsIn(v, found)
        }
    }
    return found
}

const sorted = object => Object.fromEntries(Object.entries(object).sort(([a], [b]) => a.localeCompare(b)))

// Returns { contract, missing }: missing lists every route, parameter or property with no description.
export function buildContract(server, docs) {
    const missing = []
    const paths = {}
    for (const [route, item] of Object.entries(server.paths)) {
        if (!isPublicRoute(route)) continue
        paths[route] = {}
        for (const [method, op] of Object.entries(item)) {
            const doc = docs.methods.get(op.operationId)
            const where = `${method.toUpperCase()} ${route}`
            if (!doc || !doc.summary) missing.push(`${where}: no summary (doc comment on ${op.operationId})`)
            const out = { tags: ['Home API'], operationId: op.operationId, summary: doc ? doc.summary : '' }
            if (op.parameters) {
                out.parameters = op.parameters.map(p => {
                    const description = doc && doc.params[p.name]
                    if (!description) missing.push(`${where}: parameter ${p.name} has no description`)
                    return { name: p.name, in: p.in, description: description || '', ...(p.required ? { required: true } : {}), schema: p.schema }
                })
            }
            out.responses = {}
            for (const [code, response] of Object.entries(op.responses)) {
                const json = response.content && response.content['application/json']
                out.responses[code] = {
                    description: code === '200' && doc && doc.returns ? doc.returns : response.description,
                    ...(json ? { content: { 'application/json': json } } : {})
                }
            }
            // Jellyfin marks each authenticated route; a route it leaves unmarked needs no token.
            out.security = op.security || []
            paths[route][method] = out
        }
    }
    Object.assign(paths, RESERVED)

    const schemas = {}
    const pending = [...refsIn(paths, new Set())]
    while (pending.length) {
        const name = pending.shift()
        if (schemas[name]) continue
        if (JELLYFIN_TYPES[name]) {
            schemas[name] = JELLYFIN_TYPES[name]
            continue
        }
        const source = server.components.schemas[name]
        if (!source) throw new Error(`the server's document has no schema ${name}`)
        const schema = JSON.parse(JSON.stringify(source))
        const doc = docs.types.get(name)
        if (doc) {
            // The plugin's own types: described from their doc comments; what cannot be null is always sent.
            if (doc.summary) schema.description = doc.summary
            else missing.push(`schema ${name} has no summary`)
            const required = []
            for (const [property, value] of Object.entries(schema.properties || {})) {
                const description = doc.params[property]
                if (description) value.description = description
                else missing.push(`schema ${name}: property ${property} has no description`)
                if (!value.nullable) required.push(property)
            }
            if (required.length) schema.required = required
        }
        schemas[name] = schema
        for (const ref of refsIn(schema, new Set())) if (!schemas[ref]) pending.push(ref)
    }

    const contract = {
        openapi: server.openapi,
        info: INFO,
        paths: sorted(paths),
        components: { schemas: sorted(schemas), securitySchemes: server.components.securitySchemes }
    }
    return { contract, missing }
}

export const serialize = contract => JSON.stringify(contract, null, 2) + '\n'

// Differences between the committed contract and the one the server implies, in plain words.
export function compareContracts(committed, built) {
    const problems = []
    const served = Object.keys(built.paths).filter(route => !RESERVED[route])
    for (const route of served) if (!committed.paths || !committed.paths[route]) problems.push(`${route} is served but docs/api/openapi.json does not describe it`)
    for (const route of Object.keys(committed.paths || {})) if (!built.paths[route]) problems.push(`docs/api/openapi.json describes ${route}, which the plugin does not serve`)
    if (!problems.length && serialize(committed) !== serialize(built)) {
        const a = serialize(committed).split('\n')
        const b = serialize(built).split('\n')
        const at = a.findIndex((line, i) => line !== b[i])
        problems.push(`docs/api/openapi.json differs from the API the plugin serves, from line ${at + 1}: committed ${JSON.stringify((a[at] || '').trim())}, served ${JSON.stringify((b[at] || '').trim())}`)
    }
    return problems
}

// --- validating responses -----------------------------------------------------------------------

function typeOf(value) {
    if (value === null) return 'null'
    if (Array.isArray(value)) return 'array'
    if (typeof value === 'number') return Number.isInteger(value) ? 'integer' : 'number'
    return typeof value
}

export function validate(schema, value, contract, at = '$', errors = []) {
    if (schema.$ref) return validate(contract.components.schemas[schema.$ref.replace('#/components/schemas/', '')], value, contract, at, errors)
    if (value === null) {
        if (!schema.nullable) errors.push(`${at} is null`)
        return errors
    }
    if (schema.allOf) schema.allOf.forEach(part => validate(part, value, contract, at, errors))
    if (schema.enum && !schema.enum.includes(value)) errors.push(`${at} is ${JSON.stringify(value)}, not one of ${schema.enum.join(', ')}`)
    const actual = typeOf(value)
    if (schema.type && !(actual === schema.type || (schema.type === 'number' && actual === 'integer'))) {
        errors.push(`${at} is ${actual}, not ${schema.type}`)
        return errors
    }
    if (actual === 'object') {
        for (const name of schema.required || []) if (!(name in value)) errors.push(`${at}.${name} is missing`)
        const properties = schema.properties || {}
        for (const [name, v] of Object.entries(value)) {
            if (properties[name]) validate(properties[name], v, contract, `${at}.${name}`, errors)
            else if (schema.additionalProperties === false) errors.push(`${at}.${name} is not in the contract`)
        }
    }
    if (actual === 'array' && schema.items) value.forEach((v, i) => validate(schema.items, v, contract, `${at}[${i}]`, errors))
    return errors
}

export function validateResponse(contract, operationId, status, body) {
    for (const item of Object.values(contract.paths)) {
        for (const op of Object.values(item)) {
            if (op.operationId !== operationId) continue
            const response = op.responses[status]
            if (!response) return [`${operationId} has no ${status} response in the contract`]
            const schema = response.content && response.content['application/json'] && response.content['application/json'].schema
            return schema ? validate(schema, body, contract) : []
        }
    }
    return [`the contract has no operation ${operationId}`]
}

// --- command line -------------------------------------------------------------------------------

function option(args, name) {
    const i = args.indexOf(`--${name}`)
    return i >= 0 ? args[i + 1] : undefined
}

function main(args) {
    const readJson = file => JSON.parse(readFileSync(file, 'utf8'))
    if (args[0] === 'schema') {
        const out = option(args, 'out')
        const { contract, missing } = buildContract(readJson(option(args, 'openapi')), readDocs(readFileSync(option(args, 'xml'), 'utf8')))
        if (missing.length) {
            missing.forEach(m => console.error(`missing description: ${m}`))
            return 1
        }
        if (args.includes('--check')) {
            let committed
            try { committed = readJson(out) } catch { committed = { paths: {} } }
            const problems = compareContracts(committed, contract)
            problems.forEach(p => console.error(p))
            if (problems.length) {
                console.error('Regenerate it with: UPDATE_API_SCHEMA=1 bash scripts/smoke-test.sh')
                return 1
            }
            return 0
        }
        writeFileSync(out, serialize(contract))
        return 0
    }
    if (args[0] === 'validate') {
        const errors = validateResponse(readJson(option(args, 'schema')), option(args, 'operation'), option(args, 'status') || '200', readJson(args[args.length - 1]))
        errors.forEach(e => console.error(e))
        return errors.length ? 1 : 0
    }
    console.error('usage: api-contract.mjs schema --openapi <file> --xml <file> --out <file> [--check]\n       api-contract.mjs validate --schema <file> --operation <id> [--status 200] <body.json>')
    return 2
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = main(process.argv.slice(2))
