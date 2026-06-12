// Cloudflare Worker for AI Model Launcher
// Provides edge caching, manifest caching in KV, and request routing

export default {
    async fetch(request, env, ctx) {
        const url = new URL(request.url);
        const path = url.pathname;

        // CORS headers
        const corsHeaders = {
            'Access-Control-Allow-Origin': '*',
            'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
            'Access-Control-Allow-Headers': 'Content-Type, If-None-Match, Range',
            'Access-Control-Expose-Headers': 'Content-Length, Content-Encoding, ETag, X-Original-Size',
        };

        // Handle OPTIONS
        if (request.method === 'OPTIONS') {
            return new Response(null, { headers: corsHeaders });
        }

        try {
            // Route: /manifest/{modelId}/{version}
            const manifestMatch = path.match(/^\/manifest\/([^\/]+)\/([^\/]+)$/);
            if (manifestMatch) {
                const [, modelId, version] = manifestMatch;
                return await handleManifest(request, env, modelId, version, corsHeaders);
            }

            // Route: /chunks/{hash}
            const chunkMatch = path.match(/^\/chunks\/([a-f0-9]{64})$/);
            if (chunkMatch) {
                const [, hash] = chunkMatch;
                return await handleChunk(request, env, hash, corsHeaders);
            }

            // Route: /patches/{modelId}/{from}-{to}/{file}
            const patchMatch = path.match(/^\/patches\/([^\/]+)\/([^-]+)-([^\/]+)\/(.+)$/);
            if (patchMatch) {
                const [, modelId, from, to, file] = patchMatch;
                return await handlePatch(request, env, modelId, from, to, file, corsHeaders);
            }

            // Route: /health
            if (path === '/health') {
                return new Response(JSON.stringify({ status: 'healthy', edge: request.cf?.colo }), {
                    headers: { ...corsHeaders, 'Content-Type': 'application/json' }
                });
            }

            // Route: /publickey
            if (path === '/publickey') {
                return await handlePublicKey(env, corsHeaders);
            }

            return new Response('Not Found', { status: 404, headers: corsHeaders });

        } catch (error) {
            return new Response(JSON.stringify({ error: error.message }), {
                status: 500,
                headers: { ...corsHeaders, 'Content-Type': 'application/json' }
            });
        }
    }
};

async function handleManifest(request, env, modelId, version, corsHeaders) {
    const cacheKey = `manifest:${modelId}:${version}`;

    // Try KV cache first (fastest)
    let manifest = await env.MANIFESTS_KV.get(cacheKey, 'arrayBuffer');

    if (!manifest) {
        // Fetch from R2
        const key = `manifests/${modelId}-${version}.manifest`;
        const object = await env.MODELS_BUCKET.get(key);

        if (!object) {
            return new Response('Manifest not found', { status: 404, headers: corsHeaders });
        }

        manifest = await object.arrayBuffer();

        // Cache in KV for 5 minutes
        await env.MANIFESTS_KV.put(cacheKey, manifest, { expirationTtl: 300 });
    }

    const etag = `"${await sha256(manifest)}"`;

    // Check If-None-Match
    if (request.headers.get('If-None-Match') === etag) {
        return new Response(null, { status: 304, headers: corsHeaders });
    }

    return new Response(manifest, {
        headers: {
            ...corsHeaders,
            'Content-Type': 'application/x-msgpack',
            'Cache-Control': 'public, max-age=300',
            'ETag': etag,
        }
    });
}

async function handleChunk(request, env, hash, corsHeaders) {
    // Chunks are immutable - aggressive caching
    const key = `chunks/${hash.substring(0, 2)}/${hash.substring(2, 4)}/${hash}`;

    // Check ETag
    if (request.headers.get('If-None-Match') === `"${hash}"`) {
        return new Response(null, { status: 304, headers: corsHeaders });
    }

    const object = await env.MODELS_BUCKET.get(key, {
        range: request.headers.get('Range'),
    });

    if (!object) {
        return new Response('Chunk not found', { status: 404, headers: corsHeaders });
    }

    const headers = {
        ...corsHeaders,
        'Content-Type': 'application/octet-stream',
        'Content-Encoding': 'zstd',
        'Cache-Control': 'public, immutable, max-age=31536000',
        'ETag': `"${hash}"`,
        'Accept-Ranges': 'bytes',
    };

    if (object.customMetadata?.['original-size']) {
        headers['X-Original-Size'] = object.customMetadata['original-size'];
    }

    // Handle range request
    if (object.range) {
        headers['Content-Range'] = `bytes ${object.range.offset}-${object.range.offset + object.range.length - 1}/${object.size}`;
        return new Response(object.body, { status: 206, headers });
    }

    headers['Content-Length'] = object.size;
    return new Response(object.body, { headers });
}

async function handlePatch(request, env, modelId, from, to, file, corsHeaders) {
    const key = `patches/${modelId}/${from}-${to}/${file}.patch`;
    const object = await env.MODELS_BUCKET.get(key);

    if (!object) {
        return new Response('Patch not found', { status: 404, headers: corsHeaders });
    }

    return new Response(object.body, {
        headers: {
            ...corsHeaders,
            'Content-Type': 'application/octet-stream',
            'Content-Length': object.size,
            'Cache-Control': 'public, immutable, max-age=31536000',
        }
    });
}

async function handlePublicKey(env, corsHeaders) {
    const object = await env.MODELS_BUCKET.get('keys/signing.pub');

    if (!object) {
        return new Response('Public key not found', { status: 404, headers: corsHeaders });
    }

    const keyData = await object.arrayBuffer();
    const keyId = (await sha256(keyData)).substring(0, 16);

    return new Response(JSON.stringify({
        keyId: keyId,
        publicKey: btoa(String.fromCharCode(...new Uint8Array(keyData)))
    }), {
        headers: {
            ...corsHeaders,
            'Content-Type': 'application/json',
            'Cache-Control': 'public, max-age=86400',
        }
    });
}

async function sha256(data) {
    const buffer = data instanceof ArrayBuffer ? data : new TextEncoder().encode(data);
    const hash = await crypto.subtle.digest('SHA-256', buffer);
    return Array.from(new Uint8Array(hash)).map(b => b.toString(16).padStart(2, '0')).join('');
}
