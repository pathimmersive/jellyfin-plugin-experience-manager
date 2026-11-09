// The home API contract's generator and validator. Run with: node --test "tests/api/*.test.mjs"
import test from 'node:test'
import assert from 'node:assert/strict'
import { buildContract, compareContracts, readDocs, serialize, validateResponse } from '../../scripts/api-contract.mjs'

// A small server document, shaped as Jellyfin generates it for a plugin.
const server = () => ({
    openapi: '3.0.4',
    paths: {
        '/ExperienceManager/Info': { get: { operationId: 'GetInfo', responses: { 200: { description: 'OK', content: { 'application/json': { schema: { $ref: '#/components/schemas/InfoResponse' } }, 'text/plain': {} } } } } },
        '/ExperienceManager/v1/Sections/{sectionId}/Items': {
            get: {
                operationId: 'GetSectionItems',
                security: [{ CustomAuthentication: ['DefaultAuthorization'] }],
                parameters: [{ name: 'sectionId', in: 'path', required: true, schema: { type: 'string' } }, { name: 'limit', in: 'query', schema: { type: 'integer' } }],
                responses: { 200: { description: 'OK', content: { 'application/json': { schema: { $ref: '#/components/schemas/BaseItemDtoQueryResult' } } } }, 404: { description: 'Not Found' } }
            }
        },
        '/ExperienceManager/Admin/Clients': { get: { operationId: 'GetClients', security: [{ CustomAuthentication: ['RequiresElevation'] }], responses: { 200: { description: 'OK' } } } },
        '/ExperienceManager/home.js': { get: { operationId: 'GetScript', responses: { 200: { description: 'OK' } } } },
        '/Items': { get: { operationId: 'GetItems', responses: { 200: { description: 'OK' } } } }
    },
    components: {
        schemas: {
            InfoResponse: { type: 'object', properties: { Version: { type: 'string' }, Features: { type: 'array', items: { type: 'string' } }, Note: { type: 'string', nullable: true } }, additionalProperties: false },
            BaseItemDtoQueryResult: { type: 'object', properties: { Items: { type: 'array', items: { $ref: '#/components/schemas/BaseItemDto' } }, TotalRecordCount: { type: 'integer' } }, additionalProperties: false },
            BaseItemDto: { type: 'object', properties: { Name: { type: 'string' } } }
        },
        securitySchemes: { CustomAuthentication: { type: 'apiKey', name: 'Authorization', in: 'header' } }
    }
})

const xml = `<doc><members>
<member name="M:Jellyfin.Plugin.ExperienceManager.Controllers.HomeApiController.GetInfo"><summary>Reports what the plugin offers.</summary><returns>The plugin's information.</returns></member>
<member name="M:Jellyfin.Plugin.ExperienceManager.Controllers.HomeApiController.GetSectionItems(System.String,System.Nullable{System.Int32})"><summary>Gets a section's items.</summary><param name="sectionId">The section, from the page.</param><param name="limit">How many, at most <c>100</c>.</param></member>
<member name="T:Jellyfin.Plugin.ExperienceManager.Home.InfoResponse"><summary>What the plugin offers.</summary><param name="Version">The plugin version.</param><param name="Features">The enabled features.</param><param name="Note">A note, if any.</param></member>
</members></doc>`

test('the contract covers the plugin\'s API routes only, described from the doc comments', () => {
    const { contract, missing } = buildContract(server(), readDocs(xml))
    assert.deepEqual(missing, [])
    assert.deepEqual(Object.keys(contract.paths), ['/ExperienceManager/Info', '/ExperienceManager/v1/Me/Preferences', '/ExperienceManager/v1/Sections/{sectionId}/Items'])
    const items = contract.paths['/ExperienceManager/v1/Sections/{sectionId}/Items'].get
    assert.equal(items.summary, 'Gets a section\'s items.')
    assert.equal(items.parameters[1].description, 'How many, at most `100`.')
    assert.deepEqual(Object.keys(contract.paths['/ExperienceManager/Info'].get.responses[200].content), ['application/json'])
    assert.equal(contract.paths['/ExperienceManager/v1/Me/Preferences'].get['x-not-yet-available'], true)
    assert.deepEqual(contract.paths['/ExperienceManager/Info'].get.security, [], 'discovery needs no token')
    assert.deepEqual(items.security, [{ CustomAuthentication: ['DefaultAuthorization'] }])
    assert.equal(contract.security, undefined)
})

test('the plugin\'s types are described, with what cannot be null required; Jellyfin\'s item is referenced, not copied', () => {
    const { contract } = buildContract(server(), readDocs(xml))
    const info = contract.components.schemas.InfoResponse
    assert.equal(info.description, 'What the plugin offers.')
    assert.equal(info.properties.Version.description, 'The plugin version.')
    assert.deepEqual(info.required, ['Version', 'Features'])
    assert.deepEqual(Object.keys(contract.components.schemas.BaseItemDto.properties || {}), [])
    assert.equal(contract.components.schemas.BaseItemDto.additionalProperties, true)
})

test('a route, parameter or property without a description is reported', () => {
    const bare = xml.replace('<param name="limit">How many, at most <c>100</c>.</param>', '').replace('<param name="Note">A note, if any.</param>', '')
    const { missing } = buildContract(server(), readDocs(bare))
    assert.deepEqual(missing, [
        'GET /ExperienceManager/v1/Sections/{sectionId}/Items: parameter limit has no description',
        'schema InfoResponse: property Note has no description'
    ])
})

test('the drift check fails on a changed parameter and on a route the contract does not describe', () => {
    const { contract } = buildContract(server(), readDocs(xml))
    const committed = JSON.parse(serialize(contract))
    assert.deepEqual(compareContracts(committed, contract), [])

    const changed = server()
    changed.paths['/ExperienceManager/v1/Sections/{sectionId}/Items'].get.parameters.pop()
    const fewer = buildContract(changed, readDocs(xml)).contract
    assert.match(compareContracts(committed, fewer)[0], /differs from the API the plugin serves/)

    const more = server()
    more.paths['/ExperienceManager/v1/Pages/{pageId}'] = { get: { operationId: 'GetInfo', responses: { 200: { description: 'OK' } } } }
    assert.deepEqual(compareContracts(committed, buildContract(more, readDocs(xml)).contract),
        ['/ExperienceManager/v1/Pages/{pageId} is served but docs/api/openapi.json does not describe it'])
})

test('responses are checked against the contract', () => {
    const { contract } = buildContract(server(), readDocs(xml))
    assert.deepEqual(validateResponse(contract, 'GetInfo', '200', { Version: '0.1.0.0', Features: ['pages'] }), [])
    assert.deepEqual(validateResponse(contract, 'GetInfo', '200', { Features: ['pages'] }), ['$.Version is missing'])
    assert.deepEqual(validateResponse(contract, 'GetInfo', '200', { Version: 1, Features: ['pages'], Extra: true }), ['$.Version is integer, not string', '$.Extra is not in the contract'])
    assert.deepEqual(validateResponse(contract, 'GetInfo', '200', { Version: 'x', Features: [3] }), ['$.Features[0] is integer, not string'])
    assert.deepEqual(validateResponse(contract, 'GetSectionItems', '200', { Items: [{ Name: 'A', AnyJellyfinField: 1 }], TotalRecordCount: 1 }), [])
    assert.deepEqual(validateResponse(contract, 'GetNothing', '200', {}), ['the contract has no operation GetNothing'])
})
