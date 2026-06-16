
// Convert Sq Ft to A-K-M
function convertSqFtToAKM(sqFt) {
    if (!sqFt || sqFt <= 0) return '0 M';

    const MARLA_PER_ACRE = 160;
    const MARLA_PER_KANAL = 20;
    const SQFT_PER_MARLA = 272.25;

    let totalMarla = sqFt / SQFT_PER_MARLA;
    const acres = Math.floor(totalMarla / MARLA_PER_ACRE);
    totalMarla = totalMarla - (acres * MARLA_PER_ACRE);
    const kanal = Math.floor(totalMarla / MARLA_PER_KANAL);
    totalMarla = totalMarla - (kanal * MARLA_PER_KANAL);
    const marla = Math.round(totalMarla * 100) / 100;

    let result = '';
    if (acres > 0) result += acres + ' A';
    if (kanal > 0) {
        if (result) result += ' - ';
        result += kanal + ' K';
    }
    if (marla > 0) {
        if (result) result += ' - ';
        result += marla + ' M';
    }

    if (!result) result = '0 M';
    return result;
}

function convertSqFtToAkmV2(sqFt) {
    if (!sqFt || sqFt <= 0) return '0 M';

    const marlaTotal = sqFt / 272.25;
    const acres = Math.floor(marlaTotal / 160);
    let remainingMarla = marlaTotal % 160;
    const kanal = Math.floor(remainingMarla / 20);
    const marla = Math.round((remainingMarla % 20) * 100) / 100;

    return [
        acres ? `${acres.toLocaleString()} A` : '',
        kanal ? `${kanal.toLocaleString()} K` : '',
        marla ? `${marla.toLocaleString()} M` : ''
    ].filter(Boolean).join(' - ') || '0 M';
}

function convertAkmStringToSqFt(akmString) {
    if (!akmString) return 0;

    let acres = 0, kanal = 0, marla = 0;

    const parts = akmString.split('-').map(p => p.trim());

    parts.forEach(part => {
        if (part.includes('A')) {
            acres = parseFloat(part.replace('A', '').replace(/,/g, '').trim()) || 0;
        }
        if (part.includes('K')) {
            kanal = parseFloat(part.replace('K', '').replace(/,/g, '').trim()) || 0;
        }
        if (part.includes('M')) {
            marla = parseFloat(part.replace('M', '').replace(/,/g, '').trim()) || 0;
        }
    });

    const totalMarla = (acres * 160) + (kanal * 20) + marla;
    return Math.round(totalMarla * 272.25);
}

function formatVal(val, type = 'hv') {
    const num = Number(val);

    const isInvalid = !Number.isFinite(num) || num <= 0;

    const formatCurrency = (value, decimals = 0) => {
        return value.toLocaleString('en-PK', {
            style: 'currency',
            currency: 'PKR',
            minimumFractionDigits: decimals,
            maximumFractionDigits: decimals
        });
    };

    if (isInvalid) {
        switch (type) {
            case 'd': return '0.00';
            case 'c': return formatCurrency(0, 0);
            case 'cd': return formatCurrency(0, 2);
            case 'p': return '0%';
            case 'pd': return '0.00%';
            case 'k': return '0K';
            case 'm': return '0M';
            default: return '0';
        }
    }

    switch (type) {
        case 'hv': // Whole value
            return num.toLocaleString();

        case 'd': // Decimal (2 places)
            return num.toLocaleString(undefined, {
                minimumFractionDigits: 2,
                maximumFractionDigits: 2
            });

        case 'c': // Currency (no decimals)
            return formatCurrency(num, 0);

        case 'cd': // Currency (2 decimals)
            return formatCurrency(num, 2);

        case 'p': // Percentage (no decimals)
            return (num * 100).toFixed(0) + '%';

        case 'pd': // Percentage (2 decimals)
            return (num * 100).toFixed(2) + '%';

        case 'k': // Thousands (1 decimal)
            return (num / 1_000).toFixed(1) + 'K';

        case 'm': // Millions (1 decimal)
            return (num / 1_000_000).toFixed(1) + 'M';

        default:
            return num.toLocaleString();
    }
}