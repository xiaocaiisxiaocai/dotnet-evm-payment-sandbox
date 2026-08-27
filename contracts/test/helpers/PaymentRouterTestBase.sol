// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {Test} from "forge-std/Test.sol";

import {PaymentRouter} from "../../src/PaymentRouter.sol";
import {TestUSDC} from "../../src/testtokens/TestUSDC.sol";
import {TestToken18} from "../../src/testtokens/TestToken18.sol";

abstract contract PaymentRouterTestBase is Test {
    uint256 internal constant PAYER_PRIVATE_KEY = 0xA11CE;
    uint256 internal constant OTHER_PRIVATE_KEY = 0xB0B;
    uint256 internal constant INITIAL_USDC_BALANCE = 10_000 * 1e6;
    uint256 internal constant INITIAL_TOKEN18_BALANCE = 10_000 * 1e18;
    bytes32 internal constant PAYMENT_ID = keccak256("payment-intent-001");
    bytes32 internal constant PERMIT_TYPEHASH =
        keccak256("Permit(address owner,address spender,uint256 value,uint256 nonce,uint256 deadline)");

    PaymentRouter internal router;
    TestUSDC internal usdc;
    TestToken18 internal token18;
    address internal payer;
    address internal merchant;

    event PaymentRecorded(
        bytes32 indexed paymentId, address indexed payer, address token, address indexed merchant, uint256 amount
    );

    function setUp() public virtual {
        router = new PaymentRouter();
        usdc = new TestUSDC();
        token18 = new TestToken18();
        payer = vm.addr(PAYER_PRIVATE_KEY);
        merchant = makeAddr("merchant");

        usdc.mint(payer, INITIAL_USDC_BALANCE);
        token18.mint(payer, INITIAL_TOKEN18_BALANCE);
    }

    function _approveAndPay(TestUSDC token, bytes32 paymentId, uint256 amount) internal {
        vm.startPrank(payer);
        token.approve(address(router), amount);
        router.pay(paymentId, address(token), merchant, amount);
        vm.stopPrank();
    }

    function _signPermit(
        TestUSDC token,
        uint256 signerPrivateKey,
        address owner,
        address spender,
        uint256 value,
        uint256 nonce,
        uint256 deadline
    ) internal view returns (uint8 v, bytes32 r, bytes32 s) {
        bytes32 structHash = keccak256(abi.encode(PERMIT_TYPEHASH, owner, spender, value, nonce, deadline));
        bytes32 digest = keccak256(abi.encodePacked("\x19\x01", token.DOMAIN_SEPARATOR(), structHash));
        return vm.sign(signerPrivateKey, digest);
    }
}
