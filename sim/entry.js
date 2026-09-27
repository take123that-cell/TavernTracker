// Tavern Tracker bridge to Firestone's Battlegrounds simulator (MIT):
// https://www.npmjs.com/package/@firestone-hs/simulate-bgs-battle
// Built into one self-contained script (tt-sim.js) that runs in a bare V8 engine inside the app.
const { AllCardsService } = require('@firestone-hs/reference-data');
const { simulateBattle, assignCards, SIMULATOR_VERSION } = require('@firestone-hs/simulate-bgs-battle');
const { CardsData } = require('@firestone-hs/simulate-bgs-battle/dist/cards/cards-data');

const cards = new AllCardsService();
let cardsData = null;
let cardsDataKey = '';

globalThis.TavernSim = {
    version: SIMULATOR_VERSION,

    /** Load Firestone's card database (JSON array text). Returns the number of cards. */
    loadCards(cardsJson) {
        cards.initializeCardsDbFromCards(JSON.parse(cardsJson));
        assignCards(cards);
        cardsData = null;
        return cards.getCards().length;
    },

    /** Run the simulation. Input and output are JSON text. */
    simulate(inputJson) {
        const input = JSON.parse(inputJson);
        // The app sends anomaly dbf ids; the simulator wants card ids.
        if (input.gameState && input.gameState.anomalyDbfIds) {
            input.gameState.anomalies = input.gameState.anomalyDbfIds
                .map((dbf) => { const c = cards.getCard(dbf); return c && c.id; })
                .filter((id) => !!id);
            delete input.gameState.anomalyDbfIds;
        }
        const tribes = (input.gameState && input.gameState.validTribes) || (input.options && input.options.validTribes) || [];
        const anomalies = (input.gameState && input.gameState.anomalies) || [];
        const key = JSON.stringify([tribes, anomalies]);
        if (!cardsData || key !== cardsDataKey) {
            cardsData = new CardsData(cards, false);
            cardsData.inititialize(tribes, anomalies);
            cardsDataKey = key;
        }
        const it = simulateBattle(input, cards, cardsData);
        let r = it.next();
        while (!r.done) r = it.next();
        const res = r.value;
        return JSON.stringify({
            won: res.wonPercent, tied: res.tiedPercent, lost: res.lostPercent,
            wonLethal: res.wonLethalPercent, lostLethal: res.lostLethalPercent,
            avgDamageWon: res.averageDamageWon, avgDamageLost: res.averageDamageLost,
            simulations: (res.won || 0) + (res.tied || 0) + (res.lost || 0),
            version: SIMULATOR_VERSION,
        });
    },
};
